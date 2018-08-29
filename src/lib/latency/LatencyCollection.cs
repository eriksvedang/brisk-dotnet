/*

MIT License

Copyright (c) 2017 Peter Bjorklund

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

*/
using System;
using System.Collections.Generic;

namespace Piot.Brisk
{
	public static class SumCollection
	{
		public static ushort Sum (ICollection<ushort> values)
		{
			var result = (ushort)0;

			foreach (var value in values)
			{
				result += value;
			}

			return result;
		}
	}

	public static class DeviationCollection
	{
		public static ushort Deviation (ICollection<ushort> values, ushort average)
		{
			var result = (ushort)0;

			foreach (var value in values)
			{
				var diff = (ushort)Math.Abs(value - average);
				result += (ushort)(diff * diff);
			}

			return (ushort) Math.Sqrt(result / values.Count);
		}
	}

	public class LatencyCollection
	{
		List<ushort> latencies = new List<ushort> ();

		bool IsQueueFull
		{
			get
			{
				return latencies.Count >= 10;
			}
		}

		public void AddLatency(ushort latencyInMilliseconds)
		{
			if (IsQueueFull)
			{
				latencies.RemoveAt(0);
			}
			latencies.Add (latencyInMilliseconds);
		}

		public bool StableLatency(out ushort latency)
		{
			latency = 0;

			if (!IsQueueFull)
			{
				Console.Error.WriteLine($"NOT FULL:{latencies.Count}");
				return false;
			}

			var sum = SumCollection.Sum (latencies);
			var average = (ushort)(sum / latencies.Count);
			var deviation = DeviationCollection.Deviation (latencies, average);
			Console.Error.WriteLine($"Deviation:{deviation}");

			if (deviation > 10)
			{
				return false;
			}

			latency = average;

			return true;
		}
	}
}
