"""Reproducible pseudo-randomness with no state to carry.

The generator's whole design rests on one property: **a value is a pure
function of its coordinates.** A bar is derived from a hash of ``(seed, symbol,
timestamp, interval)`` rather than from a stored path, so the same request
returns the same bar forever, no generated series has to be persisted, and a
backfill and an incremental collection of the same window agree by
construction. That last one is what Phase 3's idempotency gate is actually
testing, and this module is what makes it testable without a network.

A seeded ``random.Random`` cannot do that. Its output depends on how many
values were drawn before, which is to say on the shape of the request - so two
callers asking for overlapping windows would get different prices for the same
minute, which is the exact failure the property exists to prevent.

**Two primitives, both keyed rather than sequential.** ``blake2b`` turns a
tuple of coordinates into a 64-bit stream key, and splitmix64 turns a
``(key, index)`` pair into a value. blake2b is used only at stream setup - it
is a cryptographic hash and costs a microsecond - and splitmix64 is the inner
loop, where "a handful of integer operations" is the requirement. Neither is
load-bearing for secrecy; this is a dice roll with an address, not a cipher.

**Why not Python's own ``hash()``.** It is salted per process unless
``PYTHONHASHSEED`` is set, so a string hashed in the collector pod and the same
string hashed in a worker pod are different numbers. That is a determinism bug
that only appears across processes, which is to say in production and never in
a test.
"""

from __future__ import annotations

import hashlib
import math
from dataclasses import dataclass

__all__ = ["NoiseStream", "stream_key"]

_MASK64 = (1 << 64) - 1

# splitmix64's constants, as published. The golden-ratio increment and the two
# multipliers are the algorithm; changing any of them changes every price this
# silo has ever generated, which is why SYNTHETIC_GENERATOR_VERSION exists and
# has to be bumped alongside.
_GAMMA = 0x9E3779B97F4A7C15
_MIX_A = 0xBF58476D1CE4E5B9
_MIX_B = 0x94D049BB133111EB

#: 2**-53. Turns the top 53 bits of a 64-bit draw into a float in [0, 1) with
#: every representable value equally likely - 53 because that is a float64's
#: mantissa, and taking more bits would only add ones the type cannot hold.
_SCALE_53 = 1.0 / (1 << 53)


def _splitmix64(state: int) -> int:
    """One splitmix64 finalisation. Avalanches; not reversible in practice."""
    z = (state + _GAMMA) & _MASK64
    z = ((z ^ (z >> 30)) * _MIX_A) & _MASK64
    z = ((z ^ (z >> 27)) * _MIX_B) & _MASK64
    return z ^ (z >> 31)


def stream_key(*parts: object) -> int:
    """A stable 64-bit key for a named coordinate tuple.

    Stable across processes, machines and Python versions, which is the whole
    requirement - the parts are joined with a separator that cannot appear in a
    ticker or a date so that ``("AB", "C")`` and ``("A", "BC")`` are different
    streams rather than the same one.
    """
    payload = "\x1f".join(str(part) for part in parts).encode("utf-8")
    return int.from_bytes(hashlib.blake2b(payload, digest_size=8).digest(), "big")


@dataclass(frozen=True, slots=True)
class NoiseStream:
    """One addressable sequence of draws.

    A stream is identified by its key and indexed by an integer, and nothing
    else. There is no cursor, no state and no order dependence: ``draw(7)``
    answers the same whether or not ``draw(6)`` was ever asked for.
    """

    key: int

    @classmethod
    def named(cls, *parts: object) -> NoiseStream:
        return cls(stream_key(*parts))

    def derive(self, *parts: object) -> NoiseStream:
        """A sub-stream of this one.

        Used to give each field of each contract its own sequence, so that
        adding a field to a chain row does not shift the values of the fields
        beside it - which would silently change every price the generator has
        ever produced for reasons unrelated to prices.
        """
        return NoiseStream(stream_key(self.key, *parts))

    def draw(self, index: int) -> int:
        """The 64-bit value at ``index``."""
        return _splitmix64((self.key ^ _splitmix64(index & _MASK64)) & _MASK64)

    def uniform(self, index: int) -> float:
        """A float in ``[0, 1)``."""
        return (self.draw(index) >> 11) * _SCALE_53

    def integer(self, index: int, low: int, high: int) -> int:
        """An integer in ``[low, high]``, inclusive.

        Modulo rather than rejection sampling: the bias is one part in 2**64
        divided by the range, and every caller here is picking a share count or
        an open-interest figure out of noise. Rejection sampling would make the
        draw count depend on the values drawn, which is the order dependence
        this whole module exists to avoid.
        """
        if high < low:
            raise ValueError(f"empty range [{low}, {high}]")
        return low + self.draw(index) % (high - low + 1)

    def normal(self, index: int) -> float:
        """A standard normal, by Box-Muller from two independent draws.

        Box-Muller rather than the ziggurat that ``random.gauss`` uses, because
        the ziggurat consumes a *variable* number of underlying draws per
        value - so it cannot be indexed. Two draws in, one normal out, always,
        which is what makes ``normal(7)`` addressable at all.

        The second Box-Muller output is discarded. Keeping it would halve the
        draws and double the bookkeeping, and the draws are three integer
        multiplications.
        """
        # (0, 1] rather than [0, 1): log(0) is not a number, and shifting the
        # open end is cheaper than testing for it on every draw.
        u1 = 1.0 - self.uniform(2 * index)
        u2 = self.uniform(2 * index + 1)
        return math.sqrt(-2.0 * math.log(u1)) * math.cos(2.0 * math.pi * u2)
