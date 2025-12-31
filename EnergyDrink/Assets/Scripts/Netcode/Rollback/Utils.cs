using System;
using System.Diagnostics;

namespace Netcode.Rollback
{
    public readonly struct Instant : IComparable<Instant>, IEquatable<Instant>
    {
        private readonly long _timestamp;

        private Instant(long timestamp) => _timestamp = timestamp;

        public static Instant Now() => new(Stopwatch.GetTimestamp());

        public TimeSpan Elapsed =>
            TimeSpan.FromSeconds(
                (Stopwatch.GetTimestamp() - _timestamp) / (double)Stopwatch.Frequency
            );

        public int CompareTo(Instant other) => _timestamp.CompareTo(other._timestamp);

        public bool Equals(Instant other) => _timestamp == other._timestamp;

        public override bool Equals(object obj) => obj is Instant other && Equals(other);

        public override int GetHashCode() => _timestamp.GetHashCode();

        public static bool operator ==(Instant a, Instant b) => a.Equals(b);
        public static bool operator !=(Instant a, Instant b) => !a.Equals(b);

        public static bool operator <(Instant a, Instant b) => a._timestamp < b._timestamp;
        public static bool operator >(Instant a, Instant b) => a._timestamp > b._timestamp;
        public static bool operator <=(Instant a, Instant b) => a._timestamp <= b._timestamp;
        public static bool operator >=(Instant a, Instant b) => a._timestamp >= b._timestamp;

        public static Instant operator +(Instant a, TimeSpan b) =>
            new(a._timestamp + (long)(b.TotalSeconds * Stopwatch.Frequency));

        public static TimeSpan operator -(Instant a, Instant b) =>
            TimeSpan.FromSeconds(
                (a._timestamp - b._timestamp) / (double)Stopwatch.Frequency
            );
    }

    public sealed class Arc<T> where T: struct
    {
        private T _value;
        public ref T Value => ref _value;
        public Arc(in T value)
        {
            _value = value;
        }
    }

    public static class Helpers
    {
        public static ulong MillisSinceEpoch()
        {
            ulong millis = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return millis;
        }
    }
}