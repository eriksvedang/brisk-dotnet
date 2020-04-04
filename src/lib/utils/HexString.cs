namespace Piot.Brisk.Utils
{
    using System;

    public static class HexConverter
    {
        public static string ToString(byte[] data, int maxLength)
        {
            var dataToShow = data;
            var suffix = "";

            if (data.Length > maxLength)
            {
                var copyData = new byte[maxLength];
                Array.Copy(data, copyData, maxLength);
                dataToShow = copyData;
                suffix = "...";
            }

            var hexString = BitConverter.ToString(dataToShow).Replace("-", string.Empty);

            return $"{hexString}{suffix}";
        }
    }
}