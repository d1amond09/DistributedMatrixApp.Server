using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace DistributedMatrixApp.Common;

public class NetworkHelper
{
	public static async Task SendMessageAsync<T>(Socket socket, T message)
	{
		var jsonString = JsonSerializer.Serialize(message);
		var messageBytes = Encoding.UTF8.GetBytes(jsonString);
		var lengthPrefix = BitConverter.GetBytes(messageBytes.Length);

		await socket.SendAsync(new ArraySegment<byte>(lengthPrefix), SocketFlags.None);
		await socket.SendAsync(new ArraySegment<byte>(messageBytes), SocketFlags.None);
	}

	public static async Task<T?> ReceiveMessageAsync<T>(Socket socket)
	{
		var lengthPrefix = new byte[4];
		var bytesRead = await socket.ReceiveAsync(new ArraySegment<byte>(lengthPrefix), SocketFlags.None);

		if (bytesRead == 0) return default;

		var messageLength = BitConverter.ToInt32(lengthPrefix, 0);
		var messageBuffer = new byte[messageLength];

		var totalBytesReceived = 0;
		while (totalBytesReceived < messageLength)
		{
			totalBytesReceived += await socket.ReceiveAsync(
				new ArraySegment<byte>(messageBuffer, totalBytesReceived, messageLength - totalBytesReceived),
				SocketFlags.None);
		}

		var jsonString = Encoding.UTF8.GetString(messageBuffer);
		return JsonSerializer.Deserialize<T>(jsonString);
	}
}
