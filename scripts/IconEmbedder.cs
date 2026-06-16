using System;
using System.IO;
using System.Runtime.InteropServices;

class IconEmbedder
{
    static void Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: IconEmbedder <exe-path> <ico-path>");
            Environment.Exit(1);
        }

        string exePath = args[0];
        string icoPath = args[1];

        var icoData = File.ReadAllBytes(icoPath);
        var peData = File.ReadAllBytes(exePath);

        // Parse DOS header
        if (peData[0] != 'M' || peData[1] != 'Z')
        {
            Console.WriteLine("Error: Not a valid PE file");
            Environment.Exit(1);
        }

        int e_lfanew = BitConverter.ToInt32(peData, 0x3C);
        int numSections = BitConverter.ToUInt16(peData, e_lfanew + 6);
        int optHeaderSize = BitConverter.ToUInt16(peData, e_lfanew + 20);
        ushort magic = BitConverter.ToUInt16(peData, e_lfanew + 24);
        bool isPE32Plus = magic == 0x20B;
        int sectionSize = isPE32Plus ? 40 : 28;
        int sectionHeaderOffset = e_lfanew + 24 + optHeaderSize;

        // Find last section end
        int lastSectionEnd = 0;
        for (int i = 0; i < numSections; i++)
        {
            int off = sectionHeaderOffset + i * sectionSize;
            int rawSize = BitConverter.ToInt32(peData, off + 20);
            int rawOffset = BitConverter.ToInt32(peData, off + 24);
            if (rawSize > 0 && rawOffset + rawSize > lastSectionEnd)
                lastSectionEnd = rawOffset + rawSize;
        }

        // Align new section to 512 bytes
        int newRawOffset = (lastSectionEnd + 511) & ~511;
        int newRawSize = (icoData.Length + 511) & ~511;
        int newFileSize = newRawOffset + newRawSize;

        Array.Resize(ref peData, newFileSize);
        Array.Copy(icoData, 0, peData, newRawOffset, icoData.Length);

        // Create new section header
        int newSecOff = sectionHeaderOffset + numSections * sectionSize;
        for (int i = 0; i < sectionSize; i++)
            peData[newSecOff + i] = 0;

        byte[] name = System.Text.Encoding.ASCII.GetBytes(".icon\0\0\0");
        Array.Copy(name, 0, peData, newSecOff, 8);

        // Find last virtual address
        int lastVAddr = 0;
        for (int i = 0; i < numSections; i++)
        {
            int off = sectionHeaderOffset + i * sectionSize;
            int vAddr = BitConverter.ToInt32(peData, off + 12);
            int vSize = BitConverter.ToInt32(peData, off + 4);
            if (vAddr + vSize > lastVAddr) lastVAddr = vAddr + vSize;
        }
        int newVAddr = (lastVAddr + 0xFFF) & ~0xFFF;

        BitConverter.TryWriteBytes(peData.AsSpan(newSecOff + 4), (uint)icoData.Length);
        BitConverter.TryWriteBytes(peData.AsSpan(newSecOff + 12), (uint)newVAddr);
        BitConverter.TryWriteBytes(peData.AsSpan(newSecOff + 20), (uint)icoData.Length);
        BitConverter.TryWriteBytes(peData.AsSpan(newSecOff + 24), (uint)newRawOffset);
        BitConverter.TryWriteBytes(peData.AsSpan(newSecOff + 56), (uint)0x60000060);

        BitConverter.TryWriteBytes(peData.AsSpan(e_lfanew + 6), (ushort)(numSections + 1));

        File.WriteAllBytes(exePath, peData);
        Console.WriteLine($"Icon embedded: {exePath} ({icoData.Length} bytes)");
    }
}
