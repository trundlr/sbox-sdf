using System;
using System.Collections.Generic;
using System.Linq;

namespace Sandbox.Sdf.Noise
{
	public record struct CellularNoiseSdf2D( int Seed, Vector2 CellSize, float DistanceOffset, Vector2 InvCellSize ) : ISdf2D
	{
		public CellularNoiseSdf2D( int seed, Vector2 cellSize, float distanceOffset )
		: this( seed, cellSize, distanceOffset, new Vector2( 1f / cellSize.x, 1f / cellSize.y ) )
		{
		}

		public Rect Bounds => default;

		public float this[ Vector2 pos ]
		{
			get
			{
				var localPos = pos * InvCellSize;
				var cell = (
					X: (int)MathF.Floor( localPos.x ),
					Y: (int)MathF.Floor( localPos.y ));

				var cellPos = new Vector2( cell.X, cell.Y ) * CellSize;
				var cellLocalPos = pos - cellPos;

				var minDistSq = float.PositiveInfinity;

				foreach ( var offset in PointOffsets )
				{
					var feature = GetFeature( cell.X + offset.X, cell.Y + offset.Y ) + new Vector2( offset.X, offset.Y ) * CellSize;
					var distSq = (feature - cellLocalPos).LengthSquared;

					minDistSq = Math.Min( minDistSq, distSq );
				}

				return MathF.Sqrt( minDistSq ) - DistanceOffset;
			}
		}

		Vector2 GetFeature( int x, int y )
		{
			var hashX = HashCode.Combine( Seed, x, y );
			var hashY = HashCode.Combine( y, Seed, x );

			return new Vector2( (hashX & 0xffff) / 65536f, (hashY & 0xffff) / 65536f ) * CellSize;
		}

		private static (int X, int Y)[] PointOffsets { get; } = Enumerable.Range( -1, 3 )
			.SelectMany( y => Enumerable.Range( -1, 3 )
				.Select( x => (x, y) ) ).ToArray();
		
		public void WriteRaw( ref ByteStream writer, Dictionary<TypeDescription, int> sdfTypes )
		{
			writer.Write( Seed );
			writer.Write( CellSize );
			writer.Write( DistanceOffset );
		}

		public static CellularNoiseSdf2D ReadRaw( ref ByteStream reader, IReadOnlyDictionary<int, SdfReader<ISdf2D>> sdfTypes )
		{
			return new CellularNoiseSdf2D( reader.Read<int>(), reader.Read<Vector2>(), reader.Read<float>() );
		}
	}
}
