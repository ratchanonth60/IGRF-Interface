using System;
using System.Linq;

namespace IGRF_Interface.Utils
{
    public static class CrcUtils
    {
        public static byte[] CalculateModRTU_CRC(byte[] data)
        {
            UInt16 crc = 0xFFFF;
            for (int pos = 0; pos < data.Length; pos++)
            {
                crc ^= (UInt16)data[pos];
                for (int i = 8; i != 0; i--)
                {
                    if ((crc & 0x0001) != 0)
                    {
                        crc >>= 1;
                        crc ^= 0xA001;
                    }
                    else
                    {
                        crc >>= 1;
                    }
                }
            }
            // กลับด้าน Byte (Little Endian vs Big Endian) ตามโค้ดเดิมของคุณ
            byte[] result = BitConverter.GetBytes(crc);
            Array.Reverse(result);
            return result;
        }
    }
}