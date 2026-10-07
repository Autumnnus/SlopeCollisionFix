using System.ComponentModel;
using Terraria.ModLoader.Config;

namespace SlopeCollisionFix
{
	public enum ActivationMode
	{
		Auto,
		Always,
		Never
	}

	public sealed class FixConfig : ModConfig
	{
		public override ConfigScope Mode => ConfigScope.ClientSide;

		[DefaultValue(ActivationMode.Auto)]
		[ReloadRequired]
		public ActivationMode Activation { get; set; }
	}
}
