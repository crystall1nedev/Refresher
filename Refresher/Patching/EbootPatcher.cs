using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Text;
using System.Text.RegularExpressions;
using ELFSharp.ELF;
using ELFSharp.ELF.Sections;
using Refresher.Verification;

namespace Refresher.Patching;

public partial class EbootPatcher : IPatcher
{
    private readonly Lazy<List<PatchTargetInfo>> _targets;

    public EbootPatcher(Stream stream)
    {
        if (!stream.CanRead || !stream.CanSeek || !stream.CanWrite)
            throw new ArgumentException("Stream must be readable, seekable and writable", nameof(stream));

        this.Stream = stream;

        this.Stream.Position = 0;

        this._targets = new Lazy<List<PatchTargetInfo>>(() => FindPatchableElements(stream));
    }

    public Stream Stream { get; }

    [GeneratedRegex("^https?[^\\x00]//([0-9a-zA-Z.:].*)/?([0-9a-zA-Z_]*)$", RegexOptions.Compiled)]
    private static partial Regex UrlMatch();

    [GeneratedRegex("[a-zA-Z0-9!@#$%^&*()?/<>~\\[\\]]", RegexOptions.Compiled)]
    private static partial Regex DigestMatch();

    /// <summary>
    /// Finds a set of URLs and Digest keys in the given file, excluding C printf strings.
    /// </summary>
    /// <param name="file">A seekable stream containing the file to look through</param>
    /// <returns>A list of the URLs and Digest keys</returns>
    private static List<PatchTargetInfo> FindPatchableElements(Stream file)
    {
        long start = Stopwatch.GetTimestamp();
        
        file.Position = 0;
        using IELF? elf = ELFReader.Load(file, false);
        file.Position = 0;

        //Buffer the stream with a size of 4096
        BufferedStream bufferedStream = new(file, 4096);
        BinaryReader reader = new(bufferedStream);
        
        // The string "http" in ASCII, as an int
        int httpInt = BitConverter.ToInt32("http"u8);

        // The first 4-byte word of the word "cookie", which seems to always proceed digest keys
        int cookieStartInt = BitConverter.ToInt32("cook"u8);

        //If string "lbpk" in ASCII, as an int
        int lbpkStartInt = BitConverter.ToInt32("lbpk"u8);

        //The main LBPK port
        int lbpkPort = BitConverter.ToInt32(new byte[] { 0x00, 0x00, 0x27, 0x42 });

        //Found positions of `http` in the binary
        List<long> possibleUrls = new();
        //Found positions of `cook` in the binary
        List<long> cookiePositions = new();
        //Found positions of `lbpk` in the binary
        List<long> lbpkPositions = new();
        //Found positions of the LBPK server port
        List<long> lbpkPortPositions = new();

        long read = 0;
        
        //Create an array twice the size of the data we are wanting to check
        Span<byte> arr = new byte[8];
        while (reader.Read(arr) == arr.Length)
        {
            long? found = null;
            for (int i = 0; i < 5; i++)
            {
                int check = BitConverter.ToInt32(arr[i..(i + 4)]);

                if (check == httpInt)
                {
                    possibleUrls.Add(read + i - 4);
                    found = read + i - 4;
                    break;
                }

                if (check == cookieStartInt)
                {
                    cookiePositions.Add(read + i - 4);
                    found = read + i - 4;
                    break;
                }

                if (check == lbpkPort)
                {
                    lbpkPortPositions.Add(read + i - 4);
                    found = read + i - 4;
                    break;
                }
                
                // ReSharper disable once InvertIf
                if (check == lbpkStartInt)
                {
                    lbpkPositions.Add(read + i - 4);
                    found = read + i - 4;
                    break;
                }
            }

            //Seek 4 bytes after the position we started at, or 4 bytes after the starting index of a match
            reader.BaseStream.Seek(found == null ? read + 4 : found.Value + 4, SeekOrigin.Begin);

            read += arr.Length;
        }
        
        List<PatchTargetInfo> foundItems = new();
        FilterValidUrls(reader, possibleUrls, foundItems);
        FindDigestAroundCookie(reader, cookiePositions, foundItems);
        FindLbpkDomains(reader, lbpkPositions, foundItems);
        FindLbpkPorts(reader, lbpkPortPositions, foundItems);

        long end = Stopwatch.GetTimestamp();
        Console.WriteLine($"Detecting patchables took {(double)(end - start) / (double)Stopwatch.Frequency} seconds!");
        return foundItems;
    }

    private static int SwapEndianness(int num)
    {
        return (num & 0x000000FF) << 24 |
               (num & 0x0000FF00) << 8 |
               (num & 0x00FF0000) >> 8 |
               (int)((num & 0xFF000000) >> 24);
    }

    private static void FindLbpkPorts(BinaryReader reader, List<long> foundLbpkPortPositions, List<PatchTargetInfo> foundItems)
    {
        foreach (long foundPosition in foundLbpkPortPositions)
        {
            reader.BaseStream.Position = foundPosition;
            int port1 = SwapEndianness(reader.ReadInt32());
            int port2 = SwapEndianness(reader.ReadInt32());

            if (port1 != 10050 || port2 != 10051) continue;
            
            foundItems.Add(new PatchTargetInfo
            {
                Offset = foundPosition,
                Length = sizeof(int) * 2,
                Data = null,
                Type = PatchTargetType.DoubleNumeric32BitPort,
            });
        }
    }
    
    private static void FindLbpkDomains(BinaryReader reader, List<long> foundLbpkPositions, List<PatchTargetInfo> foundItems)
    {
        string host = "lbpk.ps3.online.scea.com";
        
        foreach (long foundPosition in foundLbpkPositions)
        {
            bool tooLong = false;

            int len = 0;

            reader.BaseStream.Position = foundPosition;

            while (reader.ReadByte() != 0)
            {
                len++;

                if (len > host.Length + 1)
                {
                    tooLong = true;
                    break;
                }
            }

            //Skip this string and continue
            if (tooLong) continue;
            
            //Keep reading until we arent at a null byte
            while (reader.ReadByte() == 0) len++;

            //Remove one from length to make sure to leave a single null byte after
            len -= 1;
            
            reader.BaseStream.Position = foundPosition;

            //`len` at this point is the amount of bytes that are actually available to repurpose
            //This includes all extra null bytes except for the last one

            byte[] match = new byte[len];
            if (reader.Read(match) < len) continue;
            string str = Encoding.UTF8.GetString(match).TrimEnd('\0');

            if (str != host) continue; // Ignore unknown domains

            foundItems.Add(new PatchTargetInfo
            {
                Length = len,
                Offset = foundPosition,
                Data = str,
                Type = PatchTargetType.Host,
            });
        }
    }

    private static void FilterValidUrls(BinaryReader reader, List<long> foundPossibleUrlPositions, List<PatchTargetInfo> foundItems)
    {
        
        foreach (long foundPosition in foundPossibleUrlPositions)
        {
            bool tooLong = false;
        
            int len = 0;

            reader.BaseStream.Position = foundPosition;

            //Find the first null byte
            while (reader.ReadByte() != 0)
            {
                len++;

                if (len > 100)
                {
                    tooLong = true;
                    break;
                }
            }

            if (tooLong) continue;

            //Keep reading until we arent at a null byte
            while (reader.ReadByte() == 0) len++;

            //Remove one from length to make sure to leave a single null byte after
            len -= 1;
            
            reader.BaseStream.Position = foundPosition;

            //`len` at this point is the amount of bytes that are actually available to repurpose
            //This includes all extra null bytes except for the last one

            byte[] match = new byte[len];
            if (reader.Read(match) < len) continue;
            string str = Encoding.UTF8.GetString(match).TrimEnd('\0');

            if (str.Contains('%')) continue; // Ignore printf strings, e.g. %s

            if (UrlMatch().Matches(str).Count != 0)
                foundItems.Add(new PatchTargetInfo
                {
                    Length = len,
                    Offset = foundPosition,
                    Data = str,
                    Type = PatchTargetType.Url,
                });
        }
    }

    private static void FindDigestAroundCookie(BinaryReader reader, List<long> foundPossibleCookiePositions, List<PatchTargetInfo> foundItems)
    {
        foreach (long foundPosition in foundPossibleCookiePositions)
        {
            reader.BaseStream.Position = foundPosition;

            byte[] cookieBuf = new byte[8];
            //If we didnt read enough or what we read isnt "cookie\0\0"
            if (reader.Read(cookieBuf) < cookieBuf.Length || !"cookie\0\0"u8.SequenceEqual(cookieBuf))
            {
                //Skip this possibility
                continue;
            }

            const int checkSize = 1000;

            //Go back half the check size in bytes (so that `cookie` is in the middle)
            reader.BaseStream.Position -= checkSize / 2;

            byte[] checkArr = new byte[checkSize];
            Span<byte> toCheck = checkArr.AsSpan().Slice(0, reader.Read(checkArr));

            Regex regex = DigestMatch();
            for (int i = 0; i < toCheck.Length; i++)
            {
                //Skip all null bytes
                while (i < toCheck.Length && toCheck[i] == 0) i++;

                int len = 0;
                int start = i;

                //Go over all non-null bytes
                while (i < toCheck.Length && toCheck[i] != 0)
                {
                    len++;
                    i++;
                }

                if (len != 18)
                    continue;

                string str = Encoding.UTF8.GetString(toCheck.Slice(start, len));

                //If theres exactly 18 matches,
                if (regex.Matches(str).Count == 18)
                {
                    //Then we have found a digest key
                    foundItems.Add(new PatchTargetInfo
                    {
                        Length = len,
                        Offset = reader.BaseStream.Position - checkSize + start,
                        Data = str,
                        Type = PatchTargetType.Digest,
                    });
                }
            }
        }
    }

    /// <summary>
    ///     Checks the contents of the EBOOT to verify that it is patchable.
    /// </summary>
    /// <returns>A list of issues and notes about the EBOOT.</returns>
    [Pure]
    public List<Message> Verify(string url, bool patchDigest)
    {
        List<Message> messages = new();

        this.Stream.Position = 0;
        Class output = ELFReader.CheckELFType(this.Stream);
        if (output == Class.NotELF)
            messages.Add(new Message(MessageLevel.Warning, 
                                     "File is not a valid ELF!"));

        // Check url
        if (url.EndsWith('/'))
            messages.Add(new Message(MessageLevel.Error,
                                     "URI cannot end with a trailing slash, invalid requests will be sent to the server"));
        
        //Try to create an absolute URI, if it fails, its not a valid URI
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            messages.Add(new Message(MessageLevel.Error, "URI is not valid"));

        // If there are no Url or Host targets, we cant patch
        // ReSharper disable once SimplifyLinqExpressionUseAll
        if (!this._targets.Value.Any(x => x.Type is PatchTargetType.Url or PatchTargetType.Host))
            messages.Add(new Message(MessageLevel.Error,
                                     "Could not find any urls in the EBOOT. Nothing can be changed. If you believe this is a bug, please open an issue."));

        if (this._targets.Value.Any(x => x.Type == PatchTargetType.Url && x.Length < url.Length))
            messages.Add(new Message(MessageLevel.Error,
                "The URL is too long to fit in the EBOOT. Please use a shorter URL."));
        
        if (uri != null && this._targets.Value.Any(x => x.Type == PatchTargetType.Host && x.Length < uri.Host.Length))
            messages.Add(new Message(MessageLevel.Error,
                "The URL host is too long to fit in the EBOOT. Please use a shorter host."));

        // If we are patching digest, check if we found a digest in the EBOOT
        if (patchDigest && this._targets.Value.Count(x => x.Type == PatchTargetType.Digest) == 0)
            messages.Add(new Message(MessageLevel.Warning,
                                     "Could not find the digest in the EBOOT. Resulting EBOOT may still work depending on game."));

        return messages;
    }

    public void Patch(string url, bool patchDigest)
    {
        using BinaryWriter writer = new(this.Stream);

        PatchFromInfoList(writer, this._targets.Value, url, patchDigest);
    }

    private static void PatchFromInfoList(BinaryWriter writer, List<PatchTargetInfo> targets, string url, bool patchDigest)
    {
        Uri uri = new(url);
        
        byte[] urlAsBytes = Encoding.UTF8.GetBytes(url);
        byte[] hostAsBytes = Encoding.UTF8.GetBytes(uri.Host);
        int port = SwapEndianness(uri.Port);
        foreach (PatchTargetInfo target in targets.Where(x => x.Type == PatchTargetType.Url))
        {
            writer.BaseStream.Position = target.Offset;
            writer.Write(urlAsBytes);

            //Terminate the rest of the string
            for (int i = 0; i < target.Length - urlAsBytes.Length; i++) writer.Write('\0');
        }

        foreach (PatchTargetInfo target in targets.Where(x => x.Type == PatchTargetType.Host))
        {
            writer.BaseStream.Position = target.Offset;
            writer.Write(hostAsBytes);
            
            //Terminate the rest of the string
            for (int i = 0; i < target.Length - hostAsBytes.Length; i++) writer.Write('\0');
        }
        
        foreach (PatchTargetInfo target in targets.Where(x => x.Type == PatchTargetType.DoubleNumeric32BitPort))
        {
            writer.BaseStream.Position = target.Offset;
            
            Debug.Assert(target.Length == sizeof(int) * 2);
            
            //TODO: expose somehow the ability to specify separate ports here, this WILL break at some point, as im not sure what the second port is used for yet
            
            //Write the first port
            writer.Write(port);
            //Write the second port
            writer.Write(port);
        }

        if (patchDigest)
        {
            ReadOnlySpan<byte> digestBytes = "CustomServerDigest"u8;

            foreach (PatchTargetInfo target in targets.Where(x => x.Type == PatchTargetType.Digest))
            {
                Debug.Assert(target.Length == 18);

                writer.BaseStream.Position = target.Offset;
                writer.Write(digestBytes);
            }
        }
    }
}