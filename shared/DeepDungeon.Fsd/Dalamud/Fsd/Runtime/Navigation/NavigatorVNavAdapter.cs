using DeepDungeon.Fsd.Core;
using System;
using System.Numerics;
using VNav = DeepDungeon.Fsd.Dalamud.moveHelper.VNav;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Navigation
{
	public sealed class NavigatorVNavAdapter : INavigator, IDisposable
	{
        private readonly Func<Vector3, bool, bool> _start;
        private readonly Func<bool> _pathfindInProgress;
        private readonly Action _cancelPathfinding;
        private readonly Action _stop;
        private bool _cancelPending;

        public NavigatorVNavAdapter() : this(StartNative,
            VNav.SimpleMove.PathfindInProgress, VNav.Nav.PathfindCancelAll, VNav.Path.Stop) { }

        internal NavigatorVNavAdapter(Func<Vector3, bool, bool> start, Func<bool> pathfindInProgress,
            Action cancelPathfinding, Action stop)
        {
            _start = start;
            _pathfindInProgress = pathfindInProgress;
            _cancelPathfinding = cancelPathfinding;
            _stop = stop;
        }
        private static bool StartNative(Vector3 destination, bool alignCamera)
        {
            var tolerance = Service.PluginInterface.GetIpcSubscriber<float, object>("vnavmesh.Path.SetTolerance");
            if (tolerance.HasAction) tolerance.InvokeAction(NavigationRange.MovementTolerance);
            return VNav.SimpleMove.PathfindAndMoveTo(destination, alignCamera);
        }

		public bool PathfindAndMoveTo(Vector3 dest, bool alignCamera)
		{
			try
			{
                bool started = _start(dest, alignCamera);
				_cancelPending |= started;
				return started;
			}
			catch
			{
				return false;
			}
		}

		public bool RepathTo(Vector3 dest, bool alignCamera)
		{
			CancelAll();
			return PathfindAndMoveTo(dest, alignCamera);
		}

		public void CancelAll() => CancelAll(cancelPathfinding: true);

		internal void CancelAll(bool cancelPathfinding)
		{
			if (!_cancelPending)
				return;

			// A pending SimpleMove could start following its route after Path.Stop.
			if (cancelPathfinding && _pathfindInProgress())
                _cancelPathfinding();
            _stop();
			_cancelPending = false;
		}

		public void Dispose()
		{
			try { CancelAll(); } catch { }
		}
	}
}
