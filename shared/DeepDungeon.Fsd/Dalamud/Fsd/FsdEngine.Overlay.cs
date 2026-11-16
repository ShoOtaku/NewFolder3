using System.Numerics;
using DeepDungeon.Fsd.Dalamud.Coord;

namespace DeepDungeon.Fsd.Dalamud
{
	internal partial class FsdEngine
	{
		private const uint ColHead = 0xFF00E0FF;     // cyan-ish head
		private const uint ColOpen = 0xFF8CFFB0;     // soft green
		private const uint ColDone = 0xFF555A60;     // dim
		private const uint ColRetry = 0xFF40A0FF;    // orange
		private const uint ColDead = 0xFF5050F0;     // red-ish
		private const uint ColArrow = 0xFF00E0FF;

		/// <summary>
		/// World-space overlay for the live site pool: every site gets a
		/// kind + value + score label at its world position; the committed
		/// head gets an arrow from the player.
		/// </summary>
		public void DrawSitePoolOverlay()
		{
			var driver = ObjectiveSiteDriver.Active;
			var player = Service.LocalPlayer;
			if (driver == null || player == null)
				return;

			foreach (var s in driver.Sites)
			{
				var isHead = driver.Head is { } h
					&& ObjectiveSiteDriver.SiteKey(h) == ObjectiveSiteDriver.SiteKey(s);
				var color = isHead ? ColHead : s.Status switch
				{
					ObjectiveSiteStatus.Open => ColOpen,
					ObjectiveSiteStatus.Retryable => ColRetry,
					ObjectiveSiteStatus.Done
						or ObjectiveSiteStatus.Falsified => ColDone,
					_ => ColDead,
				};
				var scLabel = driver.LastScores.TryGetValue(
					ObjectiveSiteDriver.SiteKey(s), out var sc)
					? $" sc={sc:0.00}" : "";   // no fabricated zero score
				driver.DwellSeconds.TryGetValue(
					ObjectiveSiteDriver.SiteKey(s), out var dw);
				var dwLabel = s.Kind == ObjectiveSiteKind.Candidate
					? $" dw={dw:0.0}/3" : "";
				var label = $"{(isHead ? "» " : "")}{s.Kind} " +
					$"v={s.Value:0.0}{scLabel}{dwLabel} {s.Status}";
				WorldDrawHelper.DrawWorldText(
					s.Position + new Vector3(0, 1.2f, 0), color, label);
				WorldDrawHelper.DrawWorldCircle(
					s.Position, isHead ? 1.6f : 0.8f, color, 10, 1.5f);
			}

			if (driver.Head is { } head)
			{
				WorldDrawHelper.DrawWorldLine(
					player.Position, head.Position, ColArrow, 3f);
				WorldDrawHelper.DrawWorldCircle(
					head.Position, 2.2f, ColArrow, 12, 2.5f);
			}

			// Bitmap navigation diagnostics — published only while the
			// runtime owner exists. Nearby boundary edges (≤32m), the raw
			// A* cell path, the smoothed path, the current leg/next corner,
			// the commanded target and the arrival circle.
			var nav = NavDiag.Navigation;
			var map = NavDiag.Map;
			if (nav != null && map != null)
			{
				var pos = player.Position;
				const uint ColBlockedEdge = 0xCCFF4060;
				const uint ColRawPath = 0xCCFF40FF;
				const uint ColSmoothPath = 0xCC40C0FF;
				foreach (var (a, b) in map.BoundarySegments)
				{
					var mx = (a.X + b.X) * 0.5f - pos.X;
					var mz = (a.Z + b.Z) * 0.5f - pos.Z;
					if (mx * mx + mz * mz <= 32f * 32f)
						WorldDrawHelper.DrawWorldLine(a, b, ColBlockedEdge, 1f);
				}
				var raw = nav.RawPath;
				for (var i = 1; i < raw.Count; i++)
					WorldDrawHelper.DrawWorldLine(raw[i - 1], raw[i], ColRawPath, 1f);
				var smoothed = nav.SmoothedPath;
				for (var i = 1; i < smoothed.Count; i++)
					WorldDrawHelper.DrawWorldLine(smoothed[i - 1], smoothed[i], ColSmoothPath, 2.5f);
				if (nav.LegIndex < smoothed.Count)
					WorldDrawHelper.DrawWorldCircle(
						smoothed[nav.LegIndex], 0.6f, ColHead, 8, 2f);
				WorldDrawHelper.DrawWorldCircle(
					nav.CommandedTarget, 0.4f, ColSmoothPath, 8, 2f);
				if (nav.ArrivalRadius > 0f)
					WorldDrawHelper.DrawWorldCircle(
						nav.FinalDestination, nav.ArrivalRadius, ColArrow, 12, 1.5f);
			}
		}
	}
}
