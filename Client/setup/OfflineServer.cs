using System.Net;
using System.Net.Sockets;
using Game.Client;
using Godot;
using Shenanicode.Rollback.LiteNetLib;
using Space.GameCore;

namespace Space.Client;

public static class OfflineServer {
	public const ushort DefaultPort = 8153;

	private static double s_serverTime;

	public static bool IsRunning { get; private set; }

	public static bool TryStart(ushort port = DefaultPort) {
		if (IsRunning) {
			return true;
		}

		if (IsPortOccupied(port)) {
			return false;
		}

		ServerSetup.CreateAndInitialize(new LiteNetLibRemoteClientListener(port), new GodotLogger("Server"));
		IsRunning = true;
		GD.Print($"Offline server started on port {port}.");

		return true;
	}

	public static void Update(double delta) {
		if (!IsRunning) {
			return;
		}

		s_serverTime += delta;
		SRVR.Update(s_serverTime);
	}

	public static void Destroy() {
		if (!IsRunning) {
			return;
		}

		s_serverTime = 0;
		IsRunning = false;
		ServerSetup.Destroy();
		GD.Print("Offline server stopped.");
	}

	private static bool IsPortOccupied(ushort port) {
		try {
			using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));

			return false;
		} catch (SocketException) {
			return true;
		}
	}
}
