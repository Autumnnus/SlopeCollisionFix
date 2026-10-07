using System;
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using Terraria;

namespace SlopeCollisionFix
{
	// A line-for-line copy of Terraria.Collision.TileCollision (tModLoader 2026.x; the IL fingerprint is verified in CollisionFixSystem).
	// The only difference is that the "tileSolidTop && frameY != 0" check is delegated to a predicate, so Magic Storage's
	// IL edit of that exact check can be reproduced.
	internal static class ManagedTileCollision
	{
		// On ARM64, the JIT-optimized code for this method evaluates the sloped-tile checks incorrectly. Keep it unoptimized.
		[MethodImpl(MethodImplOptions.NoOptimization | MethodImplOptions.NoInlining)]
		public static Vector2 Run(Func<Tile, bool> isNotTopOfTile, Vector2 Position, Vector2 Velocity, int Width, int Height, bool fallThrough, bool fall2, int gravDir)
		{
			Collision.up = false;
			Collision.down = false;
			Vector2 result = Velocity;
			Vector2 val = Position + Velocity;
			int value = (int)(Position.X / 16f) - 1;
			int value2 = (int)((Position.X + Width) / 16f) + 2;
			int value3 = (int)(Position.Y / 16f) - 1;
			int value4 = (int)((Position.Y + Height) / 16f) + 2;
			int num = -1;
			int num2 = -1;
			int num3 = -1;
			int num4 = -1;
			int num5 = Utils.Clamp(value, 0, Main.maxTilesX - 1);
			value2 = Utils.Clamp(value2, 0, Main.maxTilesX - 1);
			value3 = Utils.Clamp(value3, 0, Main.maxTilesY - 1);
			value4 = Utils.Clamp(value4, 0, Main.maxTilesY - 1);
			float num6 = (value4 + 3) * 16;
			Vector2 val2 = default;

			for (int i = num5; i < value2; i++)
			{
				for (int j = value3; j < value4; j++)
				{
					Tile tile = Main.tile[i, j];
					if (!tile.HasTile || tile.IsActuated || (!Main.tileSolid[tile.TileType] && (!Main.tileSolidTop[tile.TileType] || isNotTopOfTile(tile))))
						continue;

					val2.X = i * 16;
					val2.Y = j * 16;
					int num7 = 16;
					if (tile.IsHalfBlock)
					{
						val2.Y += 8f;
						num7 -= 8;
					}

					if (!(val.X + Width > val2.X) || !(val.X < val2.X + 16f) || !(val.Y + Height > val2.Y) || !(val.Y < val2.Y + num7))
						continue;

					int slope = (int)tile.Slope;
					bool flag = false;
					bool flag2 = false;
					if (slope > 2)
					{
						if (slope == 3 && Position.Y + Math.Abs(Velocity.X) >= val2.Y && Position.X >= val2.X)
							flag2 = true;

						if (slope == 4 && Position.Y + Math.Abs(Velocity.X) >= val2.Y && Position.X + Width <= val2.X + 16f)
							flag2 = true;
					}
					else if (slope > 0)
					{
						flag = true;
						if (slope == 1 && Position.Y + Height - Math.Abs(Velocity.X) <= val2.Y + num7 && Position.X >= val2.X)
							flag2 = true;

						if (slope == 2 && Position.Y + Height - Math.Abs(Velocity.X) <= val2.Y + num7 && Position.X + Width <= val2.X + 16f)
							flag2 = true;
					}

					if (flag2)
						continue;

					if (Position.Y + Height <= val2.Y)
					{
						Collision.down = true;
						if ((!(Main.tileSolidTop[tile.TileType] && fallThrough) || !(Velocity.Y <= 1f || fall2)) && num6 > val2.Y)
						{
							num3 = i;
							num4 = j;
							if (num7 < 16)
								num4++;

							if (num3 != num && !flag)
							{
								result.Y = val2.Y - (Position.Y + Height) + ((gravDir == -1) ? (-0.01f) : 0f);
								num6 = val2.Y;
							}
						}
					}
					else if (Position.X + Width <= val2.X && !Main.tileSolidTop[tile.TileType])
					{
						if (i < 1 || ((int)Main.tile[i - 1, j].Slope != 2 && (int)Main.tile[i - 1, j].Slope != 4))
						{
							num = i;
							num2 = j;
							if (num2 != num4)
								result.X = val2.X - (Position.X + Width);

							if (num3 == num)
								result.Y = Velocity.Y;
						}
					}
					else if (Position.X >= val2.X + 16f && !Main.tileSolidTop[tile.TileType])
					{
						if ((int)Main.tile[i + 1, j].Slope != 1 && (int)Main.tile[i + 1, j].Slope != 3)
						{
							num = i;
							num2 = j;
							if (num2 != num4)
								result.X = val2.X + 16f - Position.X;

							if (num3 == num)
								result.Y = Velocity.Y;
						}
					}
					else if (Position.Y >= val2.Y + num7 && !Main.tileSolidTop[tile.TileType])
					{
						Collision.up = true;
						num3 = i;
						num4 = j;
						result.Y = val2.Y + num7 - Position.Y + ((gravDir == 1) ? 0.01f : 0f);
						if (num4 == num2)
							result.X = Velocity.X;
					}
				}
			}

			return result;
		}
	}
}
