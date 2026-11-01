using HarmonyLib;
using KMod;

namespace ShowRangeLights {
	public sealed class ShowRangeLightsMod : UserMod2 {
		public override void OnLoad(Harmony harmony) {
			base.OnLoad(harmony);
			// 手动应用我们自己的补丁类
			harmony.CreateClassProcessor(typeof(LightRangeVisualizerPatches)).Patch();
		}
	}
}