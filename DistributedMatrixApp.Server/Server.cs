using System.Net;
using System.Net.Sockets;
using DistributedMatrixApp.Common;
using DistributedMatrixApp.Common.Contracts;
using MathNet.Numerics.LinearAlgebra;

namespace DistributedMatrixApp.Server;

public static class Server
{
	private static readonly int CLIENT_PORT = 8000;
	private static readonly int WORKER_PORT = 9000;
	private static readonly List<Socket> _workers = new List<Socket>();
	private static readonly object _workerLock = new object();

	public static async Task Main(string[] args)
	{
		Console.WriteLine("Calculation Server starting...");

		var workerListenerTask = ListenForWorkersAsync();

		await ListenForClientsAsync();
	}

	private static async Task ListenForWorkersAsync()
	{
		var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
		listener.Bind(new IPEndPoint(IPAddress.Any, WORKER_PORT));
		listener.Listen(10);
		Console.WriteLine($"Listening for workers on port {WORKER_PORT}...");

		while (true)
		{
			var workerSocket = await listener.AcceptAsync();
			lock (_workerLock)
			{
				_workers.Add(workerSocket);
			}
			Console.WriteLine($"Worker connected from {workerSocket.RemoteEndPoint}. Total workers: {_workers.Count}");
		}
	}

	private static async Task ListenForClientsAsync()
	{
		var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
		listener.Bind(new IPEndPoint(IPAddress.Any, CLIENT_PORT));
		listener.Listen(5);
		Console.WriteLine($"Listening for clients on port {CLIENT_PORT}...");

		while (true)
		{
			var clientSocket = await listener.AcceptAsync();
			Console.WriteLine($"Client connected from {clientSocket.RemoteEndPoint}.");
			_ = Task.Run(() => HandleClientAsync(clientSocket));
		}
	}

	private static async Task HandleClientAsync(Socket clientSocket)
	{
		try
		{
			var request = await NetworkHelper.ReceiveMessageAsync<CalculationRequest>(clientSocket);
			if (request == null) return;

			Console.WriteLine($"Received matrix {request.Matrix.Length}x{request.Matrix.Length} for calculation.");

			var stopwatch = System.Diagnostics.Stopwatch.StartNew();
			var resultMatrix = await DistributeWorkAsync(request.Matrix);
			stopwatch.Stop();

			var response = new CalculationResponse(resultMatrix.ToRowArrays(), stopwatch.ElapsedMilliseconds);
			await NetworkHelper.SendMessageAsync(clientSocket, response);
			Console.WriteLine($"Result sent to client. Time: {stopwatch.ElapsedMilliseconds} ms.");
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Error handling client: {ex.Message}");
		}
		finally
		{
			clientSocket.Shutdown(SocketShutdown.Both);
			clientSocket.Close();
		}
	}

	private static async Task<Matrix<double>> DistributeWorkAsync(double[][] matrixArray)
	{
		int matrixSize = matrixArray.Length;
		var tasks = new Queue<MatrixTask>();
		for (int i = 0; i < matrixSize; i++)
		{
			for (int j = 0; j < matrixSize; j++)
			{
				tasks.Enqueue(new MatrixTask(i, j, matrixArray));
			}
		}

		var results = Matrix<double>.Build.Dense(matrixSize, matrixSize);
		var resultsCount = 0;

		List<Socket> currentWorkers;
		lock (_workerLock)
		{
			currentWorkers = [.. _workers];
		}

		if (currentWorkers.Count == 0)
		{
			throw new InvalidOperationException("No workers available to perform the calculation.");
		}

		var workerHandlers = currentWorkers.Select(worker => Task.Run(async () =>
		{
			while (true)
			{
				MatrixTask? task;
				lock (tasks)
				{
					if (!tasks.TryDequeue(out task)) break;
				}

				await NetworkHelper.SendMessageAsync(worker, task);
				var result = await NetworkHelper.ReceiveMessageAsync<CofactorResult>(worker);

				if (result != null)
				{
					lock (results)
					{
						results[result.Row, result.Column] = result.Cofactor;
						Interlocked.Increment(ref resultsCount);
					}
				}
			}
		})).ToList();

		await Task.WhenAll(workerHandlers);

		if (resultsCount != matrixSize * matrixSize)
		{
			Console.WriteLine($"Warning: Calculation might be incomplete. Expected {matrixSize * matrixSize}, got {resultsCount}");
		}

		return results;
	}
}