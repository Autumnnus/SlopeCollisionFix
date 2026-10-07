using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

string tml = args[0];
string root = Path.GetDirectoryName(tml)!;
var dlls = Directory.GetFiles(Path.Combine(root, "Libraries"), "*.dll", SearchOption.AllDirectories)
	.Where(p => !p.Contains("/runtime") && !p.Contains("/Native/"))
	.GroupBy(p => Path.GetFileNameWithoutExtension(p)).ToDictionary(g => g.Key, g => g.Last());
AssemblyLoadContext.Default.Resolving += (ctx, name) => dlls.TryGetValue(name.Name!, out var p) ? ctx.LoadFromAssemblyPath(p) : null;
Assembly asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(tml);
Type collision = asm.GetType("Terraria.Collision")!;
MethodInfo m = collision.GetMethod("TileCollision", BindingFlags.Public | BindingFlags.Static)!;
Console.WriteLine(m);
Console.WriteLine(SlopeCollisionFix.IlFingerprint.Compute(m));
