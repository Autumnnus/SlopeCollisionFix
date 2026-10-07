# Slope Collision Fix (macOS/Linux ARM64)

A [tModLoader](https://github.com/tModLoader/tModLoader) mod that fixes hammered (sloped) block stairs acting like walls on Apple Silicon Macs and other ARM64 systems.

![icon](icon_workshop.png)

## The bug

On ARM64, when the player walks from a flat floor into a sloped block (`SlopeType.SlopeDownRight` here), they stop at the tile edge as if the slope were a full block, instead of walking up it. Regular step-up stairs (unhammered blocks) still work.

It shows up when mods that hook `Terraria.Collision.TileCollision` are enabled, for example **Calamity Mod** (`On_Collision.TileCollision`, used for boss arena walls) or **Magic Storage** (an IL edit for Storage Unit collision). Neither mod's logic is at fault: with either one enabled on its own, the same thing happens.

## Root cause (what we know)

Measured in game on macOS (Apple Silicon), tModLoader 2026.8, .NET 8:

1. A debug mod logged every frame of the player walking into the slope. `Collision.TileCollision` zeroes the horizontal velocity at `x = tileLeftEdge`, while a C# copy of the same vanilla method, called with the **exact same arguments**, correctly skips the sloped tile (`flag2 = true`) and lets the player through.
2. The hook receives the correct arguments, and Calamity's arena list is empty, so neither MonoMod's argument passing nor Calamity's logic changes the result.
3. The same managed copy, compiled normally (and therefore optimized by the JIT once hot), **also** returns the wrong result. Marking it `[MethodImpl(MethodImplOptions.NoOptimization)]` makes it return the right result, and the player can climb the stairs.

So the optimized ARM64 code the JIT generates for this method evaluates the sloped-tile condition incorrectly:

```csharp
if (slope == 2 && Position.Y + Height - Math.Abs(Velocity.X) <= val2.Y + num7 && Position.X + Width <= val2.X + 16f)
    flag2 = true;
```

Why it shows up "when mods are enabled": MonoMod compiles hooked methods into `DynamicMethod`s, which are fully optimized from the first call. An unhooked method starts at tier 0 (unoptimized) and is only re-jitted with optimizations after it gets hot. That would also explain why results looked inconsistent while toggling mods: right after a reload the code may still be unoptimized. This last part is a hypothesis; we have not isolated the exact JIT optimization involved.

## What the mod does

- Installs itself as the **outermost** `On_Collision.TileCollision` hook and **never calls `orig`**.
- Runs `ManagedTileCollision.Run`, a line-for-line copy of vanilla `TileCollision` marked `NoOptimization`.
- **Replays** every other mod's `On` hook on top of the copy, in the original order (each hook gets our copy as its `orig`).
- Reproduces Magic Storage's IL edit by calling Magic Storage's own `HackIsNotTopOfTile` predicate.

## Safety rules

The mod stays inactive (the game behaves exactly as if it were not installed) when:

| Condition | Why |
| --- | --- |
| Not ARM64 (default `Auto` mode) | The bug has only been confirmed on ARM64. |
| The IL fingerprint of `Collision.TileCollision` differs from the expected one | tModLoader changed the method, so our copy may be out of date. |
| An IL hook other than Magic Storage's exists on `TileCollision` | We bypass the method body, so its changes would be lost. |
| An `On` hook cannot be replayed (instance/closure method or unexpected signature) | We could not reproduce it exactly. |
| It cannot become the outermost hook | An outer hook could still reach the miscompiled code. |
| Activation throws | The exception is logged. |

If hooks change after activation, it falls back to the normal (unfixed) behavior until the next reload. Every decision is logged to `client.log` under `[SlopeCollisionFix]`.

The mod is `side = NoSync`: it does not need to be installed on the server or by other players, and it saves no data.

## Config

`Activation`: `Auto` (ARM64 only, default), `Always`, `Never`. Requires a mod reload.

## Building

Put this folder in `ModSources/SlopeCollisionFix` and build it from tModLoader's **Develop Mods** menu, or run `dotnet build -c Release` with the .NET 8 SDK.

### After a tModLoader update

If the log says the fingerprint differs:

1. Decompile the new `Terraria.Collision.TileCollision` and update `ManagedTileCollision.cs` if anything changed.
2. Compute the new fingerprint:

   ```sh
   cd tools/FingerprintTool
   dotnet run -c Release -- "/path/to/Steam/steamapps/common/tModLoader/tModLoader.dll"
   ```

3. Update `ExpectedFingerprint` in `CollisionFixSystem.cs`.

## Upstream

This mod is a workaround. The real fix belongs upstream:

- **.NET runtime (dotnet/runtime):** the ARM64 JIT appears to miscompile `Collision.TileCollision` in optimized code. It should be reported with a minimal repro (the condition above, `Vector2` arguments, `float` comparisons, tier 0 vs. optimized). Not reported yet.
- **tModLoader:** until the JIT is fixed, tModLoader could mark `Collision.TileCollision` with `[MethodImpl(MethodImplOptions.NoOptimization)]` on ARM64 or restructure the condition. That would not help when a mod hooks the method, though, because MonoMod's copy is compiled separately. Not reported yet.

Once either is fixed, this mod's `Auto` mode should be updated to stand down on fixed versions.

## License

[MIT](LICENSE)
