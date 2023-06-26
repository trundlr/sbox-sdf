﻿using System.Threading;
using System.Threading.Tasks;

namespace Sandbox.Sdf;

/// <summary>
/// Represents chunks in a <see cref="Sdf3DWorld"/>.
/// Each chunk contains an SDF for a sub-region of one specific volume.
/// </summary>
public partial class Sdf3DChunk : SdfChunk<Sdf3DWorld, Sdf3DChunk, Sdf3DVolume, (int X, int Y, int Z), Sdf3DArray, ISdf3D>
{
	public override Vector3 LocalPosition
	{
		get
		{
			var quality = Resource.Quality;
			return new Vector3( Key.X * quality.ChunkSize, Key.Y * quality.ChunkSize, Key.Z * quality.ChunkSize );
		}
	}

	private TranslatedSdf3D<T> ToLocal<T>( in T sdf )
		where T : ISdf3D
	{
		return sdf.Translate( new Vector3( Key.X, Key.Y, Key.Z ) * -Resource.Quality.ChunkSize );
	}

	/// <inheritdoc />
	protected override Task<bool> OnAddAsync<T>( T sdf )
	{
		return Data.AddAsync( ToLocal( sdf ) );
	}

	/// <inheritdoc />
	protected override Task<bool> OnSubtractAsync<T>( T sdf )
	{
		return Data.SubtractAsync( ToLocal( sdf ) );
	}

	/// <inheritdoc />
	protected override async Task OnUpdateMeshAsync()
	{
		var enableRenderMesh = !Game.IsServer && Resource.Material != null;
		var enableCollisionMesh = Resource.HasCollision;

		if ( !enableRenderMesh && !enableCollisionMesh )
		{
			return;
		}

		var writer = Sdf3DMeshWriter.Rent();

		try
		{
			await Data.WriteToAsync( writer, Resource );

			var renderTask = Task.CompletedTask;
			var collisionTask = Task.CompletedTask;

			if ( enableRenderMesh )
			{
				renderTask = UpdateRenderMeshesAsync( new MeshDescription( writer, Resource.Material ) );
			}

			if ( enableCollisionMesh )
			{
				var offset = new Vector3( Key.X, Key.Y, Key.Z ) * Resource.Quality.ChunkSize;

				collisionTask = GameTask.RunInThreadAsync( async () =>
				{
					var vertices = writer.VertexPositions;

					for ( var i = 0; i < vertices.Count; ++i )
					{
						vertices[i] += offset;
					}

					await UpdateCollisionMeshAsync( writer.VertexPositions, writer.Indices );
				} );
			}

			await GameTask.WhenAll( renderTask, collisionTask );
		}
		finally
		{
			writer.Return();
		}
	}
}
