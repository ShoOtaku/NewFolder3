using DeepDungeon.Fsd.Core;
using System;
using System.Numerics;
using VNav = DeepDungeon.Fsd.Dalamud.moveHelper.VNav;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Navigation
{
	public sealed class NavigatorVNavAdapter : INavigator, IDisposable
	{
        private readonly Func<Vector3, bool, bool> _start;
        private readonly MovementCoordinator coordinator;
        private readonly bool manual;

        public NavigatorVNavAdapter() : this(StartNative, MovementCoordinator.Shared) { }

        internal NavigatorVNavAdapter(bool manual) : this(StartNative, MovementCoordinator.Shared, manual) { }

        internal NavigatorVNavAdapter(Func<Vector3, bool, bool> start, MovementCoordinator coordinator, bool manual = false)
        {
            _start = start;
            this.coordinator = coordinator;
            this.manual = manual;
        }

        internal NavigatorVNavAdapter(Func<Vector3, bool, bool> start, Func<bool> pathfindInProgress,
            Action cancelPathfinding, Action stop)
        {
            _start = start;
            coordinator = new(() => (true, false, pathfindInProgress()), cancelPathfinding, stop,
                () => { }, () => PromeRadarMovementGate.IsBlocked);
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
                return coordinator.StartVNav(this, manual, () => _start(dest, alignCamera));
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
            coordinator.CancelVNav(this);
		}

		public void Dispose()
		{
			try { CancelAll(); } catch { }
		}
	}
}
