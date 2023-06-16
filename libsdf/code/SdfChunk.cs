﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sandbox.Sdf;

internal static class Static
{
	private static Texture _sWhite3D;

	public static Texture White3D => _sWhite3D ??= new Texture3DBuilder()
		.WithName( "White 3D" )
		.WithSize( 1, 1, 1 )
		.WithFormat( ImageFormat.I8 )
		.WithData( new byte[] { 255 } )
		.Finish();
}

/// <summary>
/// Base class for chunks in a <see cref="SdfWorld{TWorld,TChunk,TResource,TChunkKey,TArray,TSdf}"/>.
/// Each chunk contains an SDF for a sub-region of one specific volume / layer resource.
/// </summary>
/// <typeparam name="TWorld">Non-abstract world type</typeparam>
/// <typeparam name="TChunk">Non-abstract chunk type</typeparam>
/// <typeparam name="TResource">Volume / layer resource</typeparam>
/// <typeparam name="TChunkKey">Integer coordinates used to index a chunk</typeparam>
/// <typeparam name="TArray">Type of <see cref="SdfArray{TSdf}"/> used to contain samples</typeparam>
/// <typeparam name="TSdf">Interface for SDF shapes used to make modifications</typeparam>
public abstract partial class SdfChunk<TWorld, TChunk, TResource, TChunkKey, TArray, TSdf> : IDisposable
	where TWorld : SdfWorld<TWorld, TChunk, TResource, TChunkKey, TArray, TSdf>
	where TChunk : SdfChunk<TWorld, TChunk, TResource, TChunkKey, TArray, TSdf>, new()
	where TResource : SdfResource<TResource>
	where TChunkKey : struct
	where TArray : SdfArray<TSdf>, new()
	where TSdf : ISdf<TSdf>
{
	internal enum MainThreadTask
	{
		UpdateRenderMeshes,
		UpdateCollisionMesh,
		UpdateLayerTexture
	}

	/// <summary>
	/// Array storing SDF samples for this chunk.
	/// </summary>
	protected TArray Data { get; private set; }

	/// <summary>
	/// World that owns this chunk.
	/// </summary>
	public TWorld World { get; private set; }

	/// <summary>
	/// Volume or layer resource controlling the rendering and collision of this chunk.
	/// </summary>
	public TResource Resource { get; private set; }

	/// <summary>
	/// Position index of this chunk in the world.
	/// </summary>
	public TChunkKey Key { get; private set; }

	/// <summary>
	/// If this chunk has collision, the generated physics mesh for this chunk.
	/// </summary>
	public PhysicsShape Shape { get; set; }

	/// <summary>
	/// If this chunk is rendered, the scene object containing the generated mesh.
	/// </summary>
	public SceneObject SceneObject { get; private set; }

	public abstract Vector3 LocalPosition { get; }

	protected virtual float MaxNetworkWriteRate => 10f;

	private int _lastModificationCount;
	private readonly List<Mesh> _usedMeshes = new();

	private Task _updateMeshTask = System.Threading.Tasks.Task.CompletedTask;
	private CancellationTokenSource _updateMeshCancellationSource;

	private Dictionary<MainThreadTask, (Action Action, TaskCompletionSource Tcs)> MainThreadTasks { get; } = new();
	private Task<bool> _lastModification;

	internal void Init( TWorld world, TResource resource, TChunkKey key )
	{
		World = world;
		Resource = resource;
		Key = key;

		Data = new TArray();
		Data.Init( resource.Quality );

		OnInit();
	}

	/// <summary>
	/// Called after the chunk is added to the <see cref="World"/>.
	/// </summary>
	protected virtual void OnInit()
	{

	}

	/// <inheritdoc />
	public void Dispose()
	{
		if ( Game.IsClient && !World.IsDestroying ) World.RemoveClientChunk( (TChunk)this );

		if ( World.IsValid() && !World.IsDestroying && Shape.IsValid() ) Shape.Remove();

		if ( SceneObject.IsValid() ) SceneObject.Delete();

		Shape = null;
		SceneObject = null;
	}

	private async Task<bool> ModifyAsync( Func<bool> func )
	{
		ThreadSafe.AssertIsMainThread();

		if ( _lastModification != null )
		{
			await _lastModification;
		}

		_lastModification = World.Task.RunInThreadAsync( func );

		return await _lastModification;
	}

	/// <summary>
	/// Sets every sample in this chunk's SDF to solid or empty.
	/// </summary>
	/// <param name="solid">Solidity to set each sample to.</param>
	public Task ClearAsync( bool solid )
	{
		return ModifyAsync( () =>
		{
			Data.Clear( solid );
			return true;
		} );
	}

	/// <summary>
	/// Add a world-space shape to this chunk.
	/// </summary>
	/// <typeparam name="T">SDF type</typeparam>
	/// <param name="sdf">Shape to add</param>
	/// <returns>True if any geometry was modified</returns>
	public Task<bool> AddAsync<T>( T sdf )
		where T : TSdf
	{
		return ModifyAsync( () => OnAdd( sdf ) );
	}

	/// <summary>
	/// Implements adding a world-space shape to this chunk.
	/// </summary>
	/// <typeparam name="T">SDF type</typeparam>
	/// <param name="sdf">Shape to add</param>
	/// <returns>True if any geometry was modified</returns>
	protected abstract bool OnAdd<T>( in T sdf )
		where T : TSdf;

	/// <summary>
	/// Subtract a world-space shape from this chunk.
	/// </summary>
	/// <typeparam name="T">SDF type</typeparam>
	/// <param name="sdf">Shape to subtract</param>
	/// <returns>True if any geometry was modified</returns>
	public Task<bool> SubtractAsync<T>( T sdf )
		where T : TSdf
	{
		return ModifyAsync( () => OnSubtract( sdf ) );
	}

	/// <summary>
	/// Implements subtracting a world-space shape from this chunk.
	/// </summary>
	/// <typeparam name="T">SDF type</typeparam>
	/// <param name="sdf">Shape to subtract</param>
	/// <returns>True if any geometry was modified</returns>
	protected abstract bool OnSubtract<T>( in T sdf )
		where T : TSdf;

	[GameEvent.Tick]
	private void Tick()
	{
		UpdateMesh();
		RunMainThreadTask( MainThreadTask.UpdateCollisionMesh );
	}

	[GameEvent.PreRender]
	private void ClientPreRender()
	{
		UpdateMesh();
		RunMainThreadTask( MainThreadTask.UpdateRenderMeshes );
		RunMainThreadTask( MainThreadTask.UpdateLayerTexture );
	}

	private void UpdateMesh()
	{
		ThreadSafe.AssertIsMainThread();

		if ( Data == null || World == null || !_updateMeshTask.IsCompleted ) return;

		var modificationCount = Data.ModificationCount;

		if ( modificationCount == _lastModificationCount ) return;
		_lastModificationCount = modificationCount;

		World.ChunkMeshUpdated( (TChunk) this, false );

		if ( Resource.IsTextureSourceOnly ) return;

		_updateMeshCancellationSource?.Cancel();
		_updateMeshCancellationSource = new CancellationTokenSource();

		_updateMeshTask = UpdateMeshTaskWrapper( _updateMeshCancellationSource.Token );
	}

	private async Task UpdateMeshTaskWrapper( CancellationToken token )
	{
		token.ThrowIfCancellationRequested();

		await OnUpdateMeshAsync( token );

		token.ThrowIfCancellationRequested();

		if ( SceneObject == null || Resource.ReferencedTextures is not { Count: > 0 } ) return;

		await RunInMainThread( MainThreadTask.UpdateLayerTexture, () =>
		{
			foreach ( var reference in Resource.ReferencedTextures )
			{
				var matching = World.GetChunk( reference.Source, Key );
				UpdateLayerTexture( reference.TargetAttribute, reference.Source, matching );
			}
		} );
	}

	internal void UpdateLayerTexture( TResource resource, TChunk source )
	{
		if ( SceneObject == null || Resource.ReferencedTextures is not { Count: > 0 } ) return;

		foreach ( var reference in Resource.ReferencedTextures )
		{
			if ( reference.Source != resource ) continue;
			UpdateLayerTexture( reference.TargetAttribute, reference.Source, source );
		}
	}

	internal void UpdateLayerTexture( string targetAttribute, TResource resource, TChunk source )
	{
		ThreadSafe.AssertIsMainThread();

		if ( source != null )
		{
			if ( resource != source.Resource )
			{
				Log.Warning( $"Source chunk is using the wrong layer or volume resource" );
				return;
			}

			// ReSharper disable once CompareOfFloatsByEqualityOperator
			if ( resource.Quality.ChunkSize != Resource.Quality.ChunkSize )
			{
				Log.Warning( $"Layer {Resource.ResourceName} references {resource.ResourceName} " +
					$"as a texture source, but their chunk sizes don't match" );
				return;
			}

			SceneObject.Attributes.Set( targetAttribute, source.Data.Texture );
		}
		else
		{
			SceneObject.Attributes.Set( targetAttribute, Data.Dimensions == 3 ? Static.White3D : Texture.White );
		}

		var quality = resource.Quality;
		var arraySize = quality.ChunkResolution + SdfArray<TSdf>.Margin * 2 + 1;

		var margin = (SdfArray<TSdf>.Margin + 0.5f) / arraySize;
		var scale = 1f / quality.ChunkSize;
		var size = 1f - (SdfArray<TSdf>.Margin * 2 + 1f) / arraySize;

		var texParams = new Vector4( margin, margin, scale * size, quality.MaxDistance * 2f );

		SceneObject.Attributes.Set( $"{targetAttribute}_Params", texParams );
	}

	/// <summary>
	/// Implements updating the render / collision meshes of this chunk.
	/// </summary>
	/// <param name="token">Token to cancel outdated mesh updates</param>
	/// <returns>Task that completes when the meshes have finished updating.</returns>
	protected abstract Task OnUpdateMeshAsync( CancellationToken token );

	/// <summary>
	/// Asynchronously updates the collision shape to the defined mesh.
	/// </summary>
	/// <param name="vertices">Collision mesh vertices</param>
	/// <param name="indices">Collision mesh indices</param>
	protected Task UpdateCollisionMeshAsync( List<Vector3> vertices, List<int> indices )
	{
		return RunInMainThread( MainThreadTask.UpdateCollisionMesh, () =>
		{
			UpdateCollisionMesh( vertices, indices );
		} );
	}

	/// <summary>
	/// Updates the collision shape to the defined mesh. Must be called on the main thread.
	/// </summary>
	/// <param name="vertices">Collision mesh vertices</param>
	/// <param name="indices">Collision mesh indices</param>
	protected void UpdateCollisionMesh( List<Vector3> vertices, List<int> indices )
	{
		ThreadSafe.AssertIsMainThread();

		if ( indices.Count == 0 )
		{
			Shape?.Remove();
			Shape = null;
		}
		else
		{
			var tags = Resource.SplitCollisionTags;

			if ( !Shape.IsValid() )
			{
				Shape = World.AddMeshShape( vertices, indices );

				foreach ( var tag in tags ) Shape.AddTag( tag );
			}
			else
			{
				Shape.UpdateMesh( vertices, indices );
			}
		}
	}

	/// <summary>
	/// Updates this chunk's model to use the given set of meshes. Must be called on the main thread.
	/// </summary>
	/// <param name="meshes">Set of meshes this model should use</param>
	protected void UpdateRenderMeshes( params Mesh[] meshes )
	{
		ThreadSafe.AssertIsMainThread();

		var anyChanges = false;

		foreach ( var mesh in meshes )
		{
			if ( mesh == null || mesh.IndexCount == 0 || _usedMeshes.Contains( mesh ) )
			{
				continue;
			}

			anyChanges = true;
			break;
		}

		foreach ( var mesh in _usedMeshes )
		{
			if ( mesh.IndexCount > 0 && Array.IndexOf( meshes, mesh ) != -1 )
			{
				continue;
			}

			anyChanges = true;
			break;
		}

		if ( !anyChanges )
		{
			return;
		}

		_usedMeshes.Clear();
		_usedMeshes.AddRange( meshes.Where( x => x is { IndexCount: > 0 } ) );

		if ( _usedMeshes.Count == 0 )
		{
			SceneObject?.Delete();
			SceneObject = null;
			return;
		}

		var model = new ModelBuilder()
			.AddMeshes( _usedMeshes.ToArray() )
			.Create();

		if ( SceneObject == null )
		{
			SceneObject = new SceneObject( World.Scene, model )
			{
				Transform = new Transform( LocalPosition ),
				Batchable = Resource.ReferencedTextures is not { Count: > 0 }
			};
		}
		else
		{
			SceneObject.Model = model;
		}
	}

	private void RunMainThreadTask( MainThreadTask task )
	{
		if ( World.CurrentTickChunkTaskDuration >= TimeSpan.FromMilliseconds( 1d ) )
		{
			return;
		}

		if ( MainThreadTasks.Count == 0 )
		{
			return;
		}

		var sw = Stopwatch.StartNew();

		(Action Action, TaskCompletionSource Tcs) taskInfo;

		lock ( MainThreadTasks )
		{
			if ( !MainThreadTasks.Remove( task, out taskInfo ) )
			{
				return;
			}
		}

		try
		{
			taskInfo.Action();
			taskInfo.Tcs.SetResult();
		}
		catch ( Exception e )
		{
			taskInfo.Tcs.SetException( e );
		}
		finally
		{
			World.CurrentTickChunkTaskDuration += sw.Elapsed;
		}
	}

	internal Task RunInMainThread( MainThreadTask task, Action action )
	{
		var tcs = new TaskCompletionSource();

		lock ( MainThreadTasks )
		{
			if ( MainThreadTasks.TryGetValue( task, out var prev ) )
			{
				prev.Tcs.SetCanceled();
			}

			MainThreadTasks[task] = (action, tcs);
		}

		return tcs.Task;
	}
}
