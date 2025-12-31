using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.Assertions;

namespace Netcode.Rollback.Network
{
    public static class Compression
    {
        public static byte[] Encode(in InputBytes refInput, IEnumerable<InputBytes> pendingInput)
        {
            if (pendingInput == null) throw new ArgumentNullException(nameof(pendingInput));
            byte[] buf = DeltaEncode(refInput, pendingInput);
            // TODO: apply a run length encoding
            return buf;
        }

        static byte[] DeltaEncode(in InputBytes refInput, IEnumerable<InputBytes> pendingInput)
        {
            if (pendingInput == null) throw new ArgumentNullException(nameof(pendingInput));
            int capacity = pendingInput.Count() * refInput.Bytes.Length;
            byte[] bytes = new byte[capacity];

            int ptr = 0;
            foreach (InputBytes input in pendingInput)
            {
                Assert.AreEqual(refInput.Bytes.Length, input.Bytes.Length, "input must be same length as the reference input");
                for (int i = 0; i < refInput.Bytes.Length; i++)
                {
                    bytes[ptr++] = (byte)(refInput.Bytes[i] ^ input.Bytes[i]);
                }
            }
            return bytes;
        }

        public static byte[][] Decode(in InputBytes refInput, ReadOnlySpan<byte> data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            byte[][] buf = DeltaDecode(refInput, data);
            // TODO: apply a run length decoding
            return buf;
        }

        static byte[][] DeltaDecode(in InputBytes refInput, ReadOnlySpan<byte> data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            Assert.AreNotEqual(refInput.Bytes.Length, 0, "reference input cannot be empty");
            Assert.AreEqual(data.Length % refInput.Bytes.Length, 0, "data length must be a multiple of reference length");

            int capacity = data.Length / refInput.Bytes.Length;
            byte[][] res = new byte[capacity][];

            for (int inp = 0; inp < capacity; inp++)
            {
                res[inp] = new byte[refInput.Bytes.Length];
                for (int i = 0; i < refInput.Bytes.Length; i++)
                {
                    res[inp][i] = (byte)(refInput.Bytes[i] ^ data[refInput.Bytes.Length * inp + i]);
                }
            }
            return res;
        }
    }
}