using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ShowRangeLights {
	/// <summary>
	/// 专门用于兼容 PLib 自定义光照形状的独立类。
	/// 此类的所有反射操作都被严格包裹，即使 PLib 不存在或版本不匹配，
	/// 也只会静默返回 false，绝不会抛出异常导致游戏崩溃。
	/// </summary>
	internal static class PLibCompat {
		private static readonly bool isSupported;
		
		private static Type pLightManagerType;
		private static Type pLightShapeType;
		private static Type lightingArgsType;

		private static System.Reflection.PropertyInfo lmInstanceProp;
		private static System.Reflection.FieldInfo shapesField;
		private static System.Reflection.MethodInfo fillLightMethod;
		private static System.Reflection.ConstructorInfo lightingArgsCtor;

		static PLibCompat() {
			try {
				pLightManagerType = AccessTools.TypeByName("PeterHan.PLib.Lighting.PLightManager");
				pLightShapeType = AccessTools.TypeByName("PeterHan.PLib.Lighting.ILightShape");
				lightingArgsType = AccessTools.TypeByName("PeterHan.PLib.Lighting.LightingArgs");

				if (pLightManagerType != null && pLightShapeType != null && lightingArgsType != null) {
					lmInstanceProp = AccessTools.Property(pLightManagerType, "Instance");
					shapesField = AccessTools.Field(pLightManagerType, "shapes");
					fillLightMethod = AccessTools.Method(pLightShapeType, "FillLight");
					lightingArgsCtor = AccessTools.Constructor(lightingArgsType, new Type[] { typeof(GameObject), typeof(int), typeof(int), typeof(IDictionary<int, float>) });
					
					isSupported = lmInstanceProp != null && shapesField != null && fillLightMethod != null && lightingArgsCtor != null;
				}
			} catch (Exception e) {
				isSupported = false;
				Debug.LogWarning("[ShowRangeLights] PLib 兼容初始化失败，已安全降级。原因: " + e.Message);
			}
		}

		/// <summary>
		/// 尝试通过 PLib 获取自定义光照的格子。
		/// </summary>
		/// <returns>如果成功解析，返回 true；如果未安装 PLib 或任何原因失败，返回 false。</returns>
		internal static bool TryGetVisibleCells(GameObject owner, int origin, int range, List<int> buffer, LightShape shape) {
			if (!isSupported) return false;

			try {
				var lm = lmInstanceProp.GetValue(null);
				if (lm == null) return false;

				var shapes = shapesField.GetValue(lm) as IList;
				if (shapes == null) return false;

				// PLib 的形状 ID 是从 LightShape.Quad + 1 开始的
				int index = (int)shape - (int)LightShape.Quad - 1;
				if (index < 0 || index >= shapes.Count) return false;

				var lightShape = shapes[index];
				if (lightShape == null) return false;

				var dict = new Dictionary<int, float>();
				var args = lightingArgsCtor.Invoke(new object[] { owner, origin, range, dict });
				fillLightMethod.Invoke(lightShape, new object[] { args });

				foreach (var kvp in dict) {
					if (Grid.IsValidCell(kvp.Key))
						buffer.Add(kvp.Key);
				}
				return true;
			} catch (Exception e) {
				// 即使 FillLight 内部报错，也安全吞掉并返回 false，让主逻辑去画圆
				Debug.LogWarning("[ShowRangeLights] PLib 渲染自定义光源失败，已降级。原因: " + e.Message);
				return false;
			}
		}
	}
}