using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.ModLoader;

namespace SlopeCollisionFix
{
	// On ARM64 (Apple Silicon, ARM Linux), the optimized JIT code for Collision.TileCollision treats a sloped tile that starts
	// at floor level as a wall, so the player cannot walk up hammered block stairs. Any hook on TileCollision (On or IL)
	// makes it worse, because MonoMod compiles the hooked copy fully optimized right away.
	//
	// This system installs itself as the outermost TileCollision hook and never calls orig. Instead it runs
	// ManagedTileCollision (an unoptimized copy of the vanilla method) and replays every other mod's On hook on top of it,
	// in the original order. It refuses to activate whenever it cannot reproduce the hooked behavior exactly.
	public sealed class CollisionFixSystem : ModSystem
	{
		// Fingerprint of the original TileCollision IL in the tModLoader build this copy was written against.
		private const string ExpectedFingerprint = "1CD81175B49CC174ADD6F5A7595E4EF38A7A9F54EFAF203D945501C372E5BF4C";

		// The only IL hook we know how to reproduce: Magic Storage rewrites the first "tileSolidTop && frameY != 0" check.
		private const string MagicStorageILHookType = "MagicStorage.Edits.SolidTopCollisionHackILEdits+PatchContext";
		private const string MagicStorageEditsType = "MagicStorage.Edits.SolidTopCollisionHackILEdits";

		private static MethodDetourInfo _detourInfo;
		private static On_Collision.orig_TileCollision _chain;
		private static bool _hooked;
		private static volatile bool _bypass;

		private static MethodInfo TargetMethod => typeof(Collision).GetMethod(nameof(Collision.TileCollision), BindingFlags.Public | BindingFlags.Static);

		private static MethodInfo OurHookMethod => typeof(CollisionFixSystem).GetMethod(nameof(Hook_TileCollision), BindingFlags.NonPublic | BindingFlags.Static);

		public override void PostSetupContent()
		{
			if (!ShouldActivate(out string reason))
			{
				Mod.Logger.Info($"Inactive: {reason}");
				return;
			}

			try
			{
				TryActivate();
			}
			catch (Exception e)
			{
				Mod.Logger.Error("Activation failed; staying inactive.", e);
				Deactivate();
			}
		}

		public override void Unload() => Deactivate();

		private static bool ShouldActivate(out string reason)
		{
			ActivationMode mode = ModContent.GetInstance<FixConfig>()?.Activation ?? ActivationMode.Auto;
			Architecture arch = RuntimeInformation.ProcessArchitecture;

			switch (mode)
			{
				case ActivationMode.Never:
					reason = "disabled in the config.";
					return false;
				case ActivationMode.Always:
					reason = $"forced on in the config ({RuntimeInformation.OSDescription}, {arch}).";
					return true;
				default:
					reason = arch == Architecture.Arm64
						? $"ARM64 detected ({RuntimeInformation.OSDescription})."
						: $"the fix is only needed on ARM64 (this is {arch}). Set Activation to Always in the config to force it.";
					return arch == Architecture.Arm64;
			}
		}

		private void TryActivate()
		{
			MethodInfo target = TargetMethod;

			string fingerprint = IlFingerprint.Compute(target);
			if (fingerprint != ExpectedFingerprint)
			{
				Mod.Logger.Warn($"Collision.TileCollision differs from the version this fix was written for (fingerprint {fingerprint}). Staying inactive.");
				return;
			}

			_detourInfo = DetourManager.GetDetourInfo(target);

			// IL hooks change the method body, which we bypass entirely, so every one of them must be known and reproducible.
			Func<Tile, bool> isNotTopOfTile = static tile => tile.TileFrameY != 0;
			foreach (ILHookInfo ilHook in _detourInfo.ILHooks)
			{
				if (!ilHook.IsApplied)
					continue;

				string owner = ilHook.ManipulatorMethod.DeclaringType?.FullName;
				if (owner != MagicStorageILHookType)
				{
					Mod.Logger.Warn($"Unknown IL hook on TileCollision ({owner}::{ilHook.ManipulatorMethod.Name}). Staying inactive so its changes are not lost.");
					return;
				}

				isNotTopOfTile = GetMagicStoragePredicate();
				if (isNotTopOfTile is null)
				{
					Mod.Logger.Warn("Magic Storage's TileCollision IL hook was found, but its HackIsNotTopOfTile method was not. Staying inactive.");
					return;
				}
			}

			// Every existing On hook will end up inside ours, so each one must be replayable.
			var innerHooks = new List<On_Collision.hook_TileCollision>();
			for (DetourInfo detour = _detourInfo.FirstDetour; detour is not null; detour = detour.Next)
			{
				if (!detour.IsApplied)
					continue;

				if (detour.Entry is not MethodInfo entry || !IsReplayableHook(entry))
				{
					Mod.Logger.Warn($"TileCollision hook {detour.Entry?.DeclaringType?.FullName}::{detour.Entry?.Name} cannot be replayed safely. Staying inactive.");
					return;
				}

				innerHooks.Add(entry.CreateDelegate<On_Collision.hook_TileCollision>());
			}

			// Build the chain from the inside out: the managed copy at the core, the hooks around it in FirstDetour order.
			On_Collision.orig_TileCollision chain = (p, v, w, h, ft, f2, g) => ManagedTileCollision.Run(isNotTopOfTile, p, v, w, h, ft, f2, g);
			for (int i = innerHooks.Count - 1; i >= 0; i--)
			{
				On_Collision.hook_TileCollision hook = innerHooks[i];
				On_Collision.orig_TileCollision next = chain;
				chain = (p, v, w, h, ft, f2, g) => hook(next, p, v, w, h, ft, f2, g);
			}

			_chain = chain;
			_bypass = false;
			On_Collision.TileCollision += Hook_TileCollision;
			_hooked = true;

			// Our hook must be the outermost one; otherwise an outer hook could still reach the miscompiled code.
			if (_detourInfo.FirstDetour?.Entry != OurHookMethod)
			{
				Mod.Logger.Warn("Could not become the outermost TileCollision hook. Staying inactive.");
				Deactivate();
				return;
			}

			SetChangeWatch(true);

			string replayed = innerHooks.Count == 0 ? "none" : string.Join(", ", innerHooks.Select(h => $"{h.Method.DeclaringType?.FullName}::{h.Method.Name}"));
			bool magicStorageRule = isNotTopOfTile.Method.DeclaringType?.FullName == MagicStorageEditsType;
			Mod.Logger.Info($"Active. Replayed On hooks: {replayed}. Magic Storage IL rule: {(magicStorageRule ? "yes" : "no")}.");
		}

		private static bool IsReplayableHook(MethodInfo entry)
		{
			if (!entry.IsStatic || entry.ReturnType != typeof(Vector2))
				return false;

			Type[] expected = [typeof(On_Collision.orig_TileCollision), typeof(Vector2), typeof(Vector2), typeof(int), typeof(int), typeof(bool), typeof(bool), typeof(int)];
			return entry.GetParameters().Select(p => p.ParameterType).SequenceEqual(expected);
		}

		private static Func<Tile, bool> GetMagicStoragePredicate()
		{
			if (!ModLoader.TryGetMod("MagicStorage", out Mod magicStorage))
				return null;

			MethodInfo method = magicStorage.Code.GetType(MagicStorageEditsType)?.GetMethod("HackIsNotTopOfTile", BindingFlags.NonPublic | BindingFlags.Static);
			if (method is null || method.ReturnType != typeof(bool) || !method.GetParameters().Select(p => p.ParameterType).SequenceEqual([typeof(Tile)]))
				return null;

			return method.CreateDelegate<Func<Tile, bool>>();
		}

		private static void SetChangeWatch(bool watch)
		{
			if (_detourInfo is null)
				return;

			if (watch)
			{
				_detourInfo.DetourApplied += OnDetourChanged;
				_detourInfo.DetourUndone += OnDetourChanged;
				_detourInfo.ILHookApplied += OnILHookChanged;
				_detourInfo.ILHookUndone += OnILHookChanged;
			}
			else
			{
				_detourInfo.DetourApplied -= OnDetourChanged;
				_detourInfo.DetourUndone -= OnDetourChanged;
				_detourInfo.ILHookApplied -= OnILHookChanged;
				_detourInfo.ILHookUndone -= OnILHookChanged;
			}
		}

		private static void OnDetourChanged(DetourInfo _) => MarkStale();

		private static void OnILHookChanged(ILHookInfo _) => MarkStale();

		private static void MarkStale()
		{
			if (_bypass)
				return;

			// The hooks changed, so the replayed chain may be out of date. Fall back to the normal behavior.
			_bypass = true;
			ModContent.GetInstance<SlopeCollisionFix>()?.Logger.Warn("TileCollision hooks changed after activation. Falling back to the normal (unfixed) behavior until the next mod reload.");
		}

		private static Vector2 Hook_TileCollision(On_Collision.orig_TileCollision orig, Vector2 Position, Vector2 Velocity, int Width, int Height, bool fallThrough, bool fall2, int gravDir)
		{
			if (_bypass)
				return orig(Position, Velocity, Width, Height, fallThrough, fall2, gravDir);

			return _chain(Position, Velocity, Width, Height, fallThrough, fall2, gravDir);
		}

		private static void Deactivate()
		{
			_bypass = true;
			SetChangeWatch(false);

			if (_hooked)
			{
				On_Collision.TileCollision -= Hook_TileCollision;
				_hooked = false;
			}

			_chain = null;
			_detourInfo = null;
		}
	}
}
