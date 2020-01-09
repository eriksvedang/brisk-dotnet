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
namespace Piot.Brisk
{
    using System;

    public static class ShortSimulationFrame
    {
        public static byte FromSimulationFrame(long simulationFrame)
        {
            return (byte)(simulationFrame & 0xff);
        }

        public static int Delta(byte beforeShort, byte current)
        {
            var delta = 0;

            if (current < beforeShort)
            {
                // Wrap around
                delta = (256 - beforeShort) + current;
            }
            else
            {
                delta = current - beforeShort;
            }

            if (delta > 127)
            {
                throw new Exception($"short simulation frames are too close {beforeShort} and {current}");
            }

            return delta;
        }

        public static long ToSimulationFrame(long simulationFrameReference, byte shortSimulationFrame)
        {
            var referenceShort = FromSimulationFrame(simulationFrameReference);
            var delta = Delta(referenceShort, shortSimulationFrame);
            return simulationFrameReference + delta;
        }
    }
}
