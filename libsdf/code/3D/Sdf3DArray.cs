﻿using System;

namespace Sandbox.Sdf;

internal record struct Sdf3DArrayData( byte[] Samples, int BaseIndex, int RowStride, int SliceStride )
{
	public byte this[int x, int y, int z] => Samples[BaseIndex + x + y * RowStride + z * SliceStride];
}

internal partial class Sdf3DArray : SdfArray
{
	public Sdf3DArray()
		: base( 3 )
	{
	}

	public Sdf3DArray( WorldQuality quality )
		: base( 3, quality )
	{

	}

	protected override Texture CreateTexture()
	{
		return new Texture3DBuilder()
			.WithFormat( ImageFormat.I8 )
			.WithSize( ArraySize, ArraySize, ArraySize )
			.WithData( Samples )
			.WithAnonymous( true )
			.Finish();
	}

	protected override void UpdateTexture( Texture texture )
	{
		texture.Update( Samples );
	}

	private (int MinX, int MinY, int MinZ, int MaxX, int MaxY, int MaxZ) GetSampleRange( BBox bounds )
	{
		var (minX, maxX) = GetSampleRange( bounds.Mins.x, bounds.Maxs.x );
		var (minY, maxY) = GetSampleRange( bounds.Mins.y, bounds.Maxs.y );
		var (minZ, maxZ) = GetSampleRange( bounds.Mins.z, bounds.Maxs.z );

		return (minX, minY, minZ, maxX, maxY, maxZ);
	}

	public bool Add<T>( in T sdf )
		where T : ISdf3D
	{
		var (minX, minY, minZ, maxX, maxY, maxZ) = GetSampleRange( sdf.Bounds );
		var maxDist = Quality.MaxDistance;

		var changed = false;

		for ( var z = minZ; z < maxZ; ++z )
		{
			var worldZ = (z - Margin) * UnitSize;

			for ( var y = minY; y < maxY; ++y )
			{
				var worldY = (y - Margin) * UnitSize;

				for ( int x = minX, index = minX + y * ArraySize + z * ArraySize * ArraySize; x < maxX; ++x, ++index )
				{
					var worldX = (x - Margin) * UnitSize;
					var sampled = sdf[new Vector3( worldX, worldY, worldZ )];

					if ( sampled >= maxDist ) continue;

					var encoded = Encode( sampled );

					var oldValue = Samples[index];
					var newValue = Math.Min( encoded, oldValue );

					Samples[index] = newValue;

					changed |= oldValue != newValue;
				}
			}
		}

		if ( changed )
		{
			MarkChanged();
		}

		return changed;
	}

	public bool Subtract<T>( in T sdf )
		where T : ISdf3D
	{
		var (minX, minY, minZ, maxX, maxY, maxZ) = GetSampleRange( sdf.Bounds );
		var maxDist = Quality.MaxDistance;

		var changed = false;

		for ( var z = minZ; z < maxZ; ++z )
		{
			var worldZ = (z - Margin) * UnitSize;

			for ( var y = minY; y < maxY; ++y )
			{
				var worldY = (y - Margin) * UnitSize;

				for ( int x = minX, index = minX + y * ArraySize + z * ArraySize * ArraySize; x < maxX; ++x, ++index )
				{
					var worldX = (x - Margin) * UnitSize;
					var sampled = sdf[new Vector3( worldX, worldY, worldZ )];

					if ( sampled >= maxDist ) continue;

					var encoded = Encode( sampled );

					var oldValue = Samples[index];
					var newValue = Math.Max( (byte)(MaxEncoded - encoded), oldValue );

					Samples[index] = newValue;

					changed |= oldValue != newValue;
				}
			}
		}

		if ( changed )
		{
			MarkChanged();
		}

		return changed;
	}

	public void WriteTo( Sdf3DMeshWriter writer, Sdf2DLayer layer, bool renderMesh, bool collisionMesh )
	{
		writer.Write( new Sdf3DArrayData( Samples, Margin * (1 + ArraySize + ArraySize * ArraySize), ArraySize, ArraySize * ArraySize ),
			layer, renderMesh, collisionMesh );
	}
}
