using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;

namespace ShowRangeLights { // 独立 Mod 命名空间
	internal sealed class LightRangeVisualizer : KMonoBehaviour {
		private const string ANIM_NAME = "transferarmgrid_kanim";
		private static readonly HashedString[] PRE_ANIMS = { "grid_pre", "grid_loop" };
		private static readonly HashedString POST_ANIM = "grid_pst";
		private static readonly Color VIS_COLOR = new Color(1f, 1f, 0.4f, 1f);

		private const int MAX_POOL_SIZE = 128;
		private const int SELECTION_CHANGED_EVENT = -1503271301;

		private static int terrainVersion = 0;

		// 同一时刻只允许一个 visualizer 拥有边框，避免多个 SyncCells 互相覆盖
		private static LightRangeVisualizer borderOwner;

		// F5 光照概览检测缓存
		private static bool lightOverlayIdInitialized;
		private static bool lightOverlayIdValid;
		private static HashedString lightOverlayId;

		internal static void IncrementTerrainVersion() {
			terrainVersion++;
		}

		/// <summary>
		/// 当前是否处于 F5 光照概览模式。
		/// 首次调用时初始化 OverlayModes.Light.ID；如果 API 不存在，
		/// 则永远返回 false（主视图逻辑不受影响）。
		/// </summary>
		internal static bool IsLightOverlayActive() {
			var overlayScreen = OverlayScreen.Instance;
			if (overlayScreen == null)
				return false;
			if (!lightOverlayIdInitialized) {
				lightOverlayIdInitialized = true;
				try {
					lightOverlayId = OverlayModes.Light.ID;
					lightOverlayIdValid = true;
				} catch {
					lightOverlayIdValid = false;
				}
			}
			if (!lightOverlayIdValid)
				return false;
			// OverlayScreen 在 UI 完全初始化之前，mode getter 可能抛出空引用，
			// 这里用 try-catch 安全兜底，初始化期间视为“不在 F5 光照概览”。
			try {
				return overlayScreen.mode == lightOverlayId;
			} catch {
				return false;
			}
		}

		internal int range;
		internal LightShape shape = LightShape.Circle;
		internal int width = 4;
		internal DiscreteShadowCaster.Direction direction =
			DiscreteShadowCaster.Direction.South;
		internal Vector2 offset = Vector2.zero;

		// 是否在 OnSpawn 后立即启用（BuildingPreview 为 true；其它为 false）
		internal bool defaultEnabled;

		// true = 主视图不显示，F5 下选中才显示（BuildingComplete / LightBug 等）
		// false = 主视图按 defaultEnabled / 选中状态显示
		internal bool overlayOnly;

		// 缓存的 KSelectable。用 GetComponentInParent 以覆盖「Light2D 和 KSelectable 分层」
		// 的模组实体。不缓存 null：组件可能后到，Update 会再查一次。
		private KSelectable cachedSelectable;

		// 是否已经订阅选中事件（避免重复订阅）
		private bool selectionSubscribed;

		private readonly Dictionary<int, KBatchedAnimController> cells =
			new Dictionary<int, KBatchedAnimController>();

		private readonly HashSet<int> cachedNewCells = new HashSet<int>();
		private readonly List<int> buffer = new List<int>();
		private readonly List<int> toRemove = new List<int>();

		private int lastOrigin = Grid.InvalidCell;
		private int lastTerrainVersion = -1;

		private readonly Stack<KBatchedAnimController> effectPool =
			new Stack<KBatchedAnimController>();

		internal void CopyFrom(Light2D light, Transform root) {
			if (light == null)
				return;
			range = Mathf.CeilToInt(light.Range);
			shape = light.shape;
			width = light.Width;
			direction = light.LightDirection;
			offset = light.Offset;
			lastTerrainVersion = -1;
		}

		// 用 OnSpawn 而不是 instantiateFn 来订阅事件。
		// instantiateFn 触发的时机早于 KMonoBehaviour.InitializeComponent，
		// 此时 KMonoBehaviour.obj 还没赋值，调用 Subscribe 会空引用崩溃。
		// OnSpawn 在 Awake → Start 之后被调用，此时 obj 已就绪，可以安全订阅。
		protected override void OnSpawn() {
			base.OnSpawn();

			// 【关键】enabled 永远保持 true，由 Update 内部的 active 决定是否绘制。
			// 若在 OnSpawn 时因未选中而设 enabled = false，Unity 将不再调用 Update，
			// 取消选中后的清理逻辑会变成死代码；且若 cachedSelectable 为空
			// （组件后到），enabled = false 会导致 visualizer 永久死锁。
			enabled = true;

			// overlayOnly 的 visualizer 也需要订阅选中状态：F5 下选中才渲染
			TrySubscribe();
		}

		// 尝试查找 KSelectable 并订阅选中事件。
		// 对 defaultEnabled（BuildingPreview）直接返回：它的 active 恒为 true，
		// 不依赖 IsSelected，订阅反而会在父对象取消选中时误触 ReleaseBorderOwnership。
		// overlayOnly 的灯需要在 F5 下根据选中状态决定是否渲染，因此也要订阅。
		private void TrySubscribe() {
			if (selectionSubscribed)
				return;
			if (defaultEnabled)
				return;

			if (cachedSelectable == null)
				cachedSelectable = GetComponentInParent<KSelectable>();

			if (cachedSelectable != null) {
				selectionSubscribed = true;
				// 订阅目标必须是 KSelectable 所在的 GameObject，否则收不到它广播的事件
				Subscribe(cachedSelectable.gameObject, SELECTION_CHANGED_EVENT,
					OnSelectionChanged);
			}
		}

		private void OnSelectionChanged(object data) {
			var boxed = data as Boxed<bool>;
			if (boxed != null && !boxed.value) {
				// 取消选中时提前释放边框，避免边框残留到下一帧 Update 才清。
				// 内部网格仍由下一帧 Update 的 !active 分支清理（事件回调里做销毁
				// 有重入/卡顿风险，不在这里做）。
				ReleaseBorderOwnership();
			}
		}

		// 边框所有权管理：
		// 同一时刻只允许一个 visualizer 写边框，否则多个 visualizer 的 SyncCells
		// 会互相覆盖，导致"边框来自 A、内部网格来自 B"的错位。
		private void ReleaseBorderOwnership() {
			if (borderOwner == this) {
				borderOwner = null;
				LightBorderRenderer.Clear();
			}
		}

		private bool TryAcquireBorderOwnership() {
			if (borderOwner == null || borderOwner == this) {
				borderOwner = this;
				return true;
			}
			// 抢占规则：真实建筑（选中）优先于建造预览
			bool selfIsReal = !defaultEnabled;
			bool ownerIsReal = !borderOwner.defaultEnabled;
			if (selfIsReal && !ownerIsReal) {
				borderOwner = this;
				return true;
			}
			return false;
		}

		// 双保险：
		// 1. active 由 defaultEnabled / IsSelected / F5 状态计算
		// 2. Update 每帧都跑（enabled 恒为 true），确保取消选中后能执行清理
		private void Update() {
			// 组件后到场景：OnSpawn 时没找到 KSelectable，这里每帧补一次
			if (!selectionSubscribed && !defaultEnabled)
				TrySubscribe();

			// 【新增】判断当前 visualizer 应该处于什么状态
			bool overlayActive = IsLightOverlayActive();
			bool selected = cachedSelectable != null && cachedSelectable.IsSelected;
			bool active;
			if (overlayOnly) {
				// BuildingComplete / LightBug：主视图永远不显示，F5 下选中才显示
				active = overlayActive && selected;
			} else if (defaultEnabled) {
				// BuildingPreview：总是显示
				active = true;
			} else {
				// BuildingUnderConstruction：选中时显示（主视图和 F5 一致）
				active = selected;
			}

			// 不 active 时清理内部网格并释放边框（此处不再是死代码）
			if (!active) {
				ReleaseBorderOwnership();
				if (cells.Count > 0)
					ClearAll();
				return;
			}

			int origin = Grid.PosToCell(transform.GetPosition() + (Vector3)offset);
			if (range <= 0 || !Grid.IsValidCell(origin)) {
				ReleaseBorderOwnership();
				if (cells.Count > 0)
					ClearAll();
				return;
			}

			// 【新增】视野裁剪：F5 下可能有大量灯同时激活，视野外的直接跳过，
			// 避免 100 盏灯每帧都跑 GetVisibleCells。
			Grid.GetVisibleCellRangeInActiveWorld(out var visMin, out var visMax);
			int cx = Grid.CellColumn(origin), cy = Grid.CellRow(origin);
			if (cx < visMin.x || cx > visMax.x || cy < visMin.y || cy > visMax.y) {
				ReleaseBorderOwnership();
				if (cells.Count > 0)
					ClearAll();
				return;
			}

			// 只在 origin 或地形版本变化时重算 GetVisibleCells 并重建 cells 集合。
			// 另外，当本 visualizer 刚获得边框所有权时也强制重算一次，
			// 以保证边框和内部网格始终来自同一份数据。
			bool needRecalc = origin != lastOrigin || terrainVersion != lastTerrainVersion;
			bool isBorderOwner = borderOwner == this;
			if (!isBorderOwner) {
				if (TryAcquireBorderOwnership()) {
					isBorderOwner = true;
					needRecalc = true;
				}
			}
			if (needRecalc) {
				lastOrigin = origin;
				lastTerrainVersion = terrainVersion;

				buffer.Clear();
				bool handled = false;

				// 1. 如果是 PLib 自定义形状（大于原版 Quad），尝试交由 PLibCompat 处理
				if ((int)shape > (int)LightShape.Quad) {
					try {
						handled = PLibCompat.TryGetVisibleCells(gameObject, origin, range, buffer, shape);
					} catch {
						handled = false; // 绝对兜底，哪怕 PLibCompat 出任何意外也不崩
					}
				}

				// 2. 如果不是自定义形状，或者 PLib 未加载/处理失败，走原版逻辑
				if (!handled) {
					LightShape scanShape = shape;
					// 兜底：如果遇到未知形状且没有 PLib，降级为圆形范围，防止原版 API 报错
					if ((int)scanShape > (int)LightShape.Quad) {
						scanShape = LightShape.Circle;
					}
					DiscreteShadowCaster.GetVisibleCells(origin, buffer, range, width, direction, scanShape, true);
				}

				cachedNewCells.Clear();
				foreach (int c in buffer)
					if (Grid.IsValidCell(c))
						cachedNewCells.Add(c);

				toRemove.Clear();
				foreach (var kv in cells)
					if (!cachedNewCells.Contains(kv.Key))
						toRemove.Add(kv.Key);
				foreach (int c in toRemove) {
					ReturnEffect(cells[c]);
					cells.Remove(c);
				}

				foreach (int c in cachedNewCells) {
					if (cells.ContainsKey(c))
						continue;
					var ctrl = GetEffect(c);
					if (ctrl == null)
						continue;
					cells[c] = ctrl;
				}

				// ==========================================
				// 同步给边框渲染器，让它在外围画虚线
				// 只有边框 owner 才写，保证边框与内部网格一致
				// ==========================================
				if (isBorderOwner) {
					LightBorderRenderer.SyncCells(cachedNewCells, origin, terrainVersion);
				}
			}
		}

		private KBatchedAnimController GetEffect(int cell) {
			var pos = Grid.CellToPosCCC(cell, Grid.SceneLayer.FXFront);
			KBatchedAnimController ctrl = null;
			while (effectPool.Count > 0 && ctrl == null)
				ctrl = effectPool.Pop();

			if (ctrl != null) {
				ctrl.transform.SetPosition(pos);
				ctrl.gameObject.SetActive(true);
			} else {
				ctrl = FXHelpers.CreateEffect(ANIM_NAME, pos, null, false,
					Grid.SceneLayer.FXFront, true);
				if (ctrl == null)
					return null;
				ctrl.destroyOnAnimComplete = false;
				ctrl.visibilityType = KAnimControllerBase.VisibilityType.Always;
				ctrl.gameObject.SetActive(true);
			}
			ctrl.Play(PRE_ANIMS, KAnim.PlayMode.Loop);
			ctrl.TintColour = VIS_COLOR;
			return ctrl;
		}

		private void ReturnEffect(KBatchedAnimController ctrl) {
			if (ctrl == null)
				return;
			ctrl.gameObject.SetActive(false);
			if (effectPool.Count < MAX_POOL_SIZE)
				effectPool.Push(ctrl);
			else
				Object.Destroy(ctrl.gameObject);
		}

		private void ClearAll() {
			foreach (var kv in cells)
				ReturnEffect(kv.Value);
			cells.Clear();
			cachedNewCells.Clear();
			lastOrigin = Grid.InvalidCell;
			lastTerrainVersion = -1;
		}

		protected override void OnDisable() {
			base.OnDisable();
			ReleaseBorderOwnership();
			ClearAll();
		}

		protected override void OnCleanUp() {
			if (selectionSubscribed && cachedSelectable != null) {
				Unsubscribe(cachedSelectable.gameObject, SELECTION_CHANGED_EVENT, OnSelectionChanged);
				selectionSubscribed = false;
			}
			cachedSelectable = null;
			ReleaseBorderOwnership();
			ClearAll();
			while (effectPool.Count > 0) {
				var ctrl = effectPool.Pop();
				if (ctrl != null)
					Object.Destroy(ctrl.gameObject);
			}
			base.OnCleanUp();
		}

		internal static void EnsureOn(GameObject target, BuildingDef def,
				bool defaultEnabled, bool overlayOnly) {
			if (target == null || def == null || def.BuildingComplete == null)
				return;
			var light = def.BuildingComplete.GetComponentInChildren<Light2D>();
			if (light == null)
				return;

			int range = Mathf.CeilToInt(light.Range);
			var shape = light.shape;
			int width = light.Width;
			var direction = light.LightDirection;
			var offset = light.Offset;

			if (target.TryGetComponent(out KPrefabID prefabID)) {
				prefabID.instantiateFn += (obj) => {
					var vis = obj.GetComponent<LightRangeVisualizer>();
					if (vis == null)
						vis = obj.AddComponent<LightRangeVisualizer>();
					vis.range = range;
					vis.shape = shape;
					vis.width = width;
					vis.direction = direction;
					vis.offset = offset;
					vis.defaultEnabled = defaultEnabled;
					vis.overlayOnly = overlayOnly;
					// 刻意不在这里设置 enabled：
					// 若立即设 enabled = false，Unity 不会调用 Start → OnSpawn，
					// 订阅就永远不会发生。让 Unity 走完生命周期；Update 里的
					// active 检查会防止 OnSpawn 之前的错显。
				};
			}
		}
	}

	public static class LightRangeVisualizerPatches {
		[HarmonyPatch(typeof(Light2D), "OnSpawn")]
		public static class Light2D_OnSpawn_Patch {
			internal static void Postfix(Light2D __instance) {
				var go = __instance.gameObject;
				if (go == null)
					return;
				var building = go.GetComponentInParent<Building>();
				var root = building != null ? building.gameObject : go;

				var vis = root.GetComponent<LightRangeVisualizer>();
				if (vis == null)
					vis = root.AddComponent<LightRangeVisualizer>();
				// 通过 Light2D.OnSpawn 路径添加的 visualizer 走"选中才显示"模式
				vis.defaultEnabled = false;
				vis.overlayOnly = true;
				vis.CopyFrom(__instance, root.transform);
			}
		}

		[HarmonyPatch(typeof(GameScenePartitioner), "OnSpawn")]
		public static class GameScenePartitioner_OnSpawn_Patch {
			internal static void Postfix(GameScenePartitioner __instance) {
				if (LightRangeVisualizer_TerrainListenerFlag.registered)
					return;
				LightRangeVisualizer_TerrainListenerFlag.registered = true;
				__instance.AddGlobalLayerListener(__instance.solidChangedLayer,
					OnTerrainChanged);
				__instance.AddGlobalLayerListener(__instance.liquidChangedLayer,
					OnTerrainChanged);
			}

			private static void OnTerrainChanged(int cell, object data) {
				LightRangeVisualizer.IncrementTerrainVersion();
			}
		}

		[HarmonyPatch(typeof(GeneratedBuildings), nameof(GeneratedBuildings.
			LoadGeneratedBuildings))]
		public static class GeneratedBuildings_LoadGeneratedBuildings_Patch {
			internal static void Postfix() {
				foreach (var def in Assets.BuildingDefs) {
					if (def == null || def.BuildingComplete == null)
						continue;
					var light = def.BuildingComplete.GetComponentInChildren<Light2D>();
					if (light == null)
						continue;
					// 建造预览：主视图总是显示
					if (def.BuildingPreview != null)
						LightRangeVisualizer.EnsureOn(def.BuildingPreview, def, true, false);
					// 待施工：主视图选中时显示
					if (def.BuildingUnderConstruction != null)
						LightRangeVisualizer.EnsureOn(def.BuildingUnderConstruction, def, false, false);
					// 已完成建筑：只在 F5 下且选中时显示
					LightRangeVisualizer.EnsureOn(def.BuildingComplete, def, false, true);
				}
			}
		}

		// ==========================================
		// 把边框渲染器挂到相机的后处理层级
		// ==========================================
		[HarmonyPatch(typeof(CameraController), "OnPrefabInit")]
		public static class CameraController_OnPrefabInit_Patch {
			internal static void Postfix(CameraController __instance) {
				LightBorderRenderer.EnsureOn(__instance.overlayNoDepthCamera);
			}
		}
	}

	internal static class LightRangeVisualizer_TerrainListenerFlag {
		internal static bool registered;
	}
}