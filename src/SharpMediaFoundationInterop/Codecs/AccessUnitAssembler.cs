using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace SharpMediaFoundationInterop.Codecs
{
    /// <summary>
    /// Gathers what a caller hands a decoder - NAL units of H.264 and H.265, with their start codes or not, or an access unit
    /// of them in Annex B; OBUs of AV1, or a temporal unit of them - into the whole access units a decoder of VideoToolbox's
    /// or MediaCodec's takes one at a time. A unit is complete as the next begins, as an AV1 frame shown comes in whole, or
    /// as it is flushed. The parameter sets are kept apart, of their ids - the newest of each - and of the SPS what it says
    /// of how many frames may come before one shown ahead of them; AV1's sequence header makes its 'av1C'.
    /// </summary>
    internal sealed class AccessUnitAssembler
    {
        private static readonly byte[] StartCode = [0, 0, 0, 1];

        private readonly VideoCodec _codec;
        private readonly bool _lengthPrefixed;
        private readonly List<byte> _unit = new List<byte>();
        private readonly List<byte> _obu = new List<byte>();
        private readonly List<Range> _nalUnits = new List<Range>();
        private readonly List<Obus.Obu> _obus = new List<Obus.Obu>();
        private bool _hasPicture;
        private long _time;

        private readonly SortedDictionary<uint, byte[]> _vps = new SortedDictionary<uint, byte[]>();
        private readonly SortedDictionary<uint, byte[]> _sps = new SortedDictionary<uint, byte[]>();
        private readonly SortedDictionary<uint, byte[]> _pps = new SortedDictionary<uint, byte[]>();
        private readonly Dictionary<uint, int> _reorderDepths = new Dictionary<uint, int>();
        private bool _reducedStillPictureHeader;

        /// <summary>A unit complete, and the time of its picture: the span is the assembler's, valid only through the call.</summary>
        public delegate void UnitHandler(ReadOnlySpan<byte> unit, long time);

        /// <summary>An AV1 sequence header: its 'av1C', of the OBU whole, and the size it says.</summary>
        public delegate void SequenceHeaderHandler(byte[] av1C, int width, int height);

        public event UnitHandler AccessUnit;
        public event SequenceHeaderHandler SequenceHeader;

        /// <param name="codec">H.264, H.265 or AV1.</param>
        /// <param name="lengthPrefixed">
        /// Whether the NAL units of a unit are each of a length of 4 bytes before them, as VideoToolbox takes them - AVCC's -
        /// rather than of a start code, as MediaCodec does.
        /// </param>
        public AccessUnitAssembler(VideoCodec codec, bool lengthPrefixed)
        {
            if (codec is not (VideoCodec.H264 or VideoCodec.H265 or VideoCodec.AV1))
                throw new ArgumentOutOfRangeException(nameof(codec), codec, "Of H.264, H.265 or AV1 alone are access units gathered");
            _codec = codec;
            _lengthPrefixed = lengthPrefixed;
        }

        /// <summary>Whether a parameter set has come that is not of the ones last read with <see cref="TakeParameterSets"/>.</summary>
        public bool ParameterSetsChanged { get; private set; }

        /// <summary>Whether there are the parameter sets a decoder is made of: an SPS and a PPS, and of H.265 a VPS.</summary>
        public bool HasParameterSets => _sps.Count > 0 && _pps.Count > 0 && (_codec != VideoCodec.H265 || _vps.Count > 0);

        /// <summary>
        /// The most frames any SPS says may come before one shown ahead of them: what a decoder handing frames out in the
        /// order they are decoded must hold back.
        /// </summary>
        public int ReorderDepth
        {
            get
            {
                int depth = 0;
                foreach (var d in _reorderDepths.Values)
                    depth = Math.Max(depth, d);
                return depth;
            }
        }

        /// <summary>The parameter sets, of H.265 the VPSs first, then the SPSs, then the PPSs, each without a start code: read, and no longer changed.</summary>
        public List<byte[]> TakeParameterSets()
        {
            ParameterSetsChanged = false;
            var sets = new List<byte[]>();
            if (_codec == VideoCodec.H265)
                sets.AddRange(_vps.Values);
            sets.AddRange(_sps.Values);
            sets.AddRange(_pps.Values);
            return sets;
        }

        /// <summary>One sample in, of the time given: units complete of it are handed to <see cref="AccessUnit"/>.</summary>
        public void Push(ReadOnlySpan<byte> data, long time)
        {
            if (_codec == VideoCodec.AV1)
                PushObus(data, time);
            else
                PushNalUnits(data, time);
        }

        /// <summary>Hands out the unit being gathered, where it has a picture: as the stream ends, or is drained.</summary>
        public void Flush()
        {
            if (_unit.Count == 0 || !_hasPicture)
                return;
            AccessUnit?.Invoke(CollectionsMarshal.AsSpan(_unit), _time);
            Clear();
        }

        /// <summary>Drops the unit being gathered, for a seek.</summary>
        public void Clear()
        {
            _unit.Clear();
            _hasPicture = false;
        }

        private void PushNalUnits(ReadOnlySpan<byte> data, long time)
        {
            NalUnits.Split(data, _nalUnits);
            bool hevc = _codec == VideoCodec.H265;
            foreach (var range in _nalUnits)
            {
                var nal = data[range];
                if (nal.Length < 2)
                    continue;

                int type = hevc ? (nal[0] >> 1) & 0x3F : nal[0] & 0x1F;
                bool isPicture = hevc ? type <= 31 : type >= 1 && type <= 5;
                bool starts = hevc
                    ? type is >= 32 and <= 35 or 39 or >= 41 and <= 44 or >= 48 and <= 55 || (isPicture && NalUnits.IsFirstHevcSlice(nal))
                    : type is 6 or 7 or 8 or 9 or >= 14 and <= 18 || (isPicture && NalUnits.IsFirstH264Slice(nal));

                // a picture's units are all of one time: one of another time is of another picture
                if (_hasPicture && (starts || (isPicture && time != _time)))
                    Flush();

                if (hevc ? type is >= 32 and <= 34 : type is 7 or 8)
                {
                    StoreParameterSet(type, nal);
                    continue; // of the decoder's configuration, not of the unit
                }
                if (hevc ? type == 35 : type == 9)
                    continue; // an access unit delimiter: the units are delimited by the decoder's input buffers

                if (_unit.Count == 0)
                    _time = time;
                if (isPicture && !_hasPicture)
                {
                    _hasPicture = true;
                    _time = time;
                }

                if (_lengthPrefixed)
                {
                    int length = nal.Length;
                    _unit.Add((byte)(length >> 24));
                    _unit.Add((byte)(length >> 16));
                    _unit.Add((byte)(length >> 8));
                    _unit.Add((byte)length);
                }
                else
                {
                    _unit.AddRange(StartCode);
                }
                foreach (byte b in nal)
                    _unit.Add(b);
            }
        }

        private void StoreParameterSet(int type, ReadOnlySpan<byte> nal)
        {
            try
            {
                SortedDictionary<uint, byte[]> sets;
                uint id;
                if (_codec == VideoCodec.H264)
                {
                    if (type == 7)
                    {
                        (id, int depth) = NalUnits.ParseH264Sps(nal);
                        _reorderDepths[id] = depth;
                        sets = _sps;
                    }
                    else
                    {
                        id = NalUnits.ParseH264PpsId(nal);
                        sets = _pps;
                    }
                }
                else if (type == 32)
                {
                    id = NalUnits.ParseHevcVpsId(nal);
                    sets = _vps;
                }
                else if (type == 33)
                {
                    (id, int depth) = NalUnits.ParseHevcSps(nal);
                    _reorderDepths[id] = depth;
                    sets = _sps;
                }
                else
                {
                    id = NalUnits.ParseHevcPpsId(nal);
                    sets = _pps;
                }

                if (sets.TryGetValue(id, out var existing) && nal.SequenceEqual(existing))
                    return;
                sets[id] = nal.ToArray();
                ParameterSetsChanged = true;
            }
            catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException)
            {
                if (Log.WarnEnabled)
                    Log.Warn($"A parameter set that could not be read was left out: {ex.Message}");
            }
        }

        private void PushObus(ReadOnlySpan<byte> data, long time)
        {
            Obus.Split(data, _obus);
            foreach (var obu in _obus)
            {
                var payload = data[obu.PayloadStart..obu.End];
                bool isFrame = obu.Type is Obus.FrameHeader or Obus.Frame;

                // a temporal unit starts with its delimiter, or its sequence header; of the low overhead format it is of one time
                if (_hasPicture && (obu.Type is Obus.TemporalDelimiter or Obus.SequenceHeader || (isFrame && time != _time)))
                    Flush();

                if (obu.Type is Obus.TemporalDelimiter or Obus.Padding)
                    continue;

                if (obu.Type == Obus.SequenceHeader)
                {
                    _obu.Clear();
                    Obus.WriteWithSize(_obu, data, obu);
                    try
                    {
                        var av1C = Obus.CreateAv1C(_obu.ToArray(), payload, out _reducedStillPictureHeader, out int width, out int height);
                        SequenceHeader?.Invoke(av1C, width, height);
                    }
                    catch (EndOfStreamException)
                    {
                        if (Log.WarnEnabled)
                            Log.Warn("A sequence header that could not be read was left out.");
                    }
                }

                if (_unit.Count == 0)
                    _time = time;
                if (isFrame && !_hasPicture)
                {
                    _hasPicture = true;
                    _time = time;
                }
                _obu.Clear();
                Obus.WriteWithSize(_obu, data, obu);
                _unit.AddRange(_obu);

                // a temporal unit has one frame shown: once it is in whole, as an OBU_FRAME is, the unit is complete
                if (isFrame)
                {
                    bool shown = Obus.IsShown(payload, _reducedStillPictureHeader, out bool showExisting);
                    if (shown && (obu.Type == Obus.Frame || showExisting))
                        Flush();
                }
            }
        }
    }
}
