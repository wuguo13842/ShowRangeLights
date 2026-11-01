using System.Collections.Generic;
using UnityEngine;

namespace ShowRangeLights {
	/// <summary>
	/// 专门用于在相机上渲染连续虚线边框的独立组件。
	/// 完全不依赖 KBatchedAnimController，使用游戏原生 Klei/PostFX/Range Shader。
	/// </summary>
	internal sealed class LightBorderRenderer : MonoBehaviour {
		private const int OCCLUSION_WIDTH = 64;
		private const int OCCLUSION_HEIGHT = 64;
		private static LightBorderRenderer instance;

		public static void EnsureOn(Camera camera) {
			if (camera == null) return;
			if (instance == null) {
				instance = camera.gameObject.AddComponent<LightBorderRenderer>();
			}
		}

		/// <summary>立即清空边框数据（取消选中 / 所有权切换时调用）。</summary>
		internal static void Clear() {
			if (instance == null) return;
			instance.currentCells.Clear();
			instance.lastOrigin = Grid.InvalidCell;
			instance.lastTerrainVersion = -1;
		}

		private Camera cachedCamera;
		private Material shader;
		private Texture2D occlusionTexture;

		private readonly HashSet<int> currentCells = new HashSet<int>();
		private int lastOrigin = Grid.InvalidCell;
		private int lastTerrainVersion = -1;

		private readonly int propHighlightColor;
		private readonly int propOcclusionParams;
		private readonly int propOcclusionTex;
		private readonly int propRangeParams;
		private readonly int propUVOffsetScale;
		private readonly int propWorldParams;

		internal LightBorderRenderer() {
			propHighlightColor = Shader.PropertyToID("_HighlightColor");
			propOcclusionParams = Shader.PropertyToID("_OcclusionParams");
			propOcclusionTex = Shader.PropertyToID("_OcclusionTex");
			propRangeParams = Shader.PropertyToID("_RangeParams");
			propUVOffsetScale = Shader.PropertyToID("_UVOffsetScale");
			propWorldParams = Shader.PropertyToID("_WorldParams");
		}

		private void Start() {
			TryGetComponent(out cachedCamera);
			shader = new Material(Shader.Find("Klei/PostFX/Range"));
			occlusionTexture = new Texture2D(OCCLUSION_WIDTH, OCCLUSION_HEIGHT, TextureFormat.Alpha8, false) {
				filterMode = FilterMode.Point,
				wrapMode = TextureWrapMode.Clamp
			};
		}

		private void OnDestroy() {
			if (shader != null) Destroy(shader);
			if (occlusionTexture != null) Destroy(occlusionTexture);
			instance = null;
		}

		// 由 LightRangeVisualizer 在 Update 里调用，同步最新的边界
		internal static void SyncCells(HashSet<int> cells, int origin, int terrainVer) {
			if (instance == null) return;
			instance.currentCells.Clear();
			foreach (var c in cells) instance.currentCells.Add(c);
			instance.lastOrigin = origin;
			instance.lastTerrainVersion = terrainVer;
		}

		internal void OnPostRender() {
			if (shader == null || currentCells.Count == 0 || lastOrigin == Grid.InvalidCell) return;

			// 将格子写入 Alpha8 纹理
			var pixels = occlusionTexture.GetRawTextureData<byte>();
			int dia = OCCLUSION_WIDTH, half = dia / 2;
			for (int i = 0; i < pixels.Length; i++) pixels[i] = 0;

			// 【关键修复】CellToXY 接受 cell 索引，PosToXY 只接受 Vector3
			Grid.CellToXY(lastOrigin, out int ox, out int oy);
			int startX = ox - half;
			int startY = oy - half;

			foreach (int c in currentCells) {
				Grid.CellToXY(c, out int cx, out int cy);
				int px = cx - startX;
				int py = cy - startY;
				if (px >= 0 && px < OCCLUSION_WIDTH && py >= 0 && py < OCCLUSION_HEIGHT) {
					pixels[py * OCCLUSION_WIDTH + px] = byte.MaxValue;
				}
			}
			occlusionTexture.Apply(false, false);

			// 设置 Shader 参数
			shader.SetColor(propHighlightColor, new Color(1f, 1f, 0f, 1f)); // 黄色
			shader.SetVector(propOcclusionParams, new Vector4(1.0f / OCCLUSION_WIDTH, 1.0f / OCCLUSION_HEIGHT, 0, 0));
			shader.SetVector(propWorldParams, new Vector4(Grid.WidthInCells, Grid.HeightInCells, 1.0f / Grid.WidthInCells, 1.0f / Grid.HeightInCells));
			shader.SetTexture(propOcclusionTex, occlusionTexture);
			shader.SetVector(propRangeParams, new Vector4(ox - half, oy - half, ox + half, oy + half));

			var bottomLeft = cachedCamera.ViewportToWorldPoint(Vector3.zero);
			var topRight = cachedCamera.ViewportToWorldPoint(Vector3.one);
			shader.SetVector(propUVOffsetScale, new Vector4(bottomLeft.x, bottomLeft.y, topRight.x - bottomLeft.x, topRight.y - bottomLeft.y));

			// 绘制全屏 Quad
			GL.PushMatrix();
			shader.SetPass(0);
			GL.LoadOrtho();
			GL.Begin(GL.TRIANGLE_STRIP);
			GL.Color(Color.white);
			GL.Vertex3(0f, 0f, 0f);
			GL.Vertex3(0f, 1f, 0f);
			GL.Vertex3(1f, 0f, 0f);
			GL.Vertex3(1f, 1f, 0f);
			GL.End();
			GL.PopMatrix();
		}
	}
}