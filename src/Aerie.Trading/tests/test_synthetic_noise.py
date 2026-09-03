"""The determinism primitives, tested for the properties everything else assumes.

``providers/synthetic/prices.py`` and ``chains.py`` are built on the claim that
a value is a pure function of its coordinates. Every determinism guarantee in
Phase 2's gate, and Phase 3's idempotency gate after it, reduces to this
module - so it is worth testing directly rather than only through the prices
that come out the far end, where a violation would look like a data bug.
"""

import subprocess
import sys

from aerie_trading.providers.synthetic.noise import NoiseStream, stream_key


def test_a_draw_does_not_depend_on_the_draws_before_it() -> None:
    # The failure mode a seeded `random.Random` has and this does not: its
    # output depends on how many values were drawn first, so two callers asking
    # for overlapping windows would get different prices for the same minute.
    stream = NoiseStream.named(1, "ZVZZT")
    ascending = [stream.normal(index) for index in range(50)]
    descending = [stream.normal(index) for index in reversed(range(50))]

    assert ascending == list(reversed(descending))
    assert NoiseStream.named(1, "ZVZZT").normal(37) == ascending[37]


def test_streams_are_separated_by_their_whole_coordinate_tuple() -> None:
    assert stream_key("AB", "C") != stream_key("A", "BC")
    assert NoiseStream.named(1, "ZVZZT").key != NoiseStream.named(2, "ZVZZT").key
    assert NoiseStream.named(1, "ZVZZT").derive("wick").key != NoiseStream.named(1, "ZVZZT").key


def test_a_key_is_stable_across_processes() -> None:
    # Python's own `hash()` is salted per process unless PYTHONHASHSEED is set,
    # which makes it a determinism bug that only appears between the collector
    # pod and the worker pod - that is to say in production and never in a
    # test. This is that test: two interpreters, two hash seeds, one answer.
    program = (
        "from aerie_trading.providers.synthetic.noise import stream_key;"
        " print(stream_key(20260902, 'synthetic', 'ZVZZT'))"
    )
    seeds = [
        subprocess.run(
            [sys.executable, "-c", program],
            capture_output=True,
            text=True,
            check=True,
            env={"PYTHONHASHSEED": seed, "PATH": ""},
        ).stdout.strip()
        for seed in ("0", "12345")
    ]

    assert seeds[0] == seeds[1] == str(stream_key(20260902, "synthetic", "ZVZZT"))


def test_uniforms_stay_inside_the_unit_interval() -> None:
    stream = NoiseStream.named("bounds")
    values = [stream.uniform(index) for index in range(10_000)]

    assert min(values) >= 0.0
    assert max(values) < 1.0


def test_integers_cover_their_range_and_never_leave_it() -> None:
    stream = NoiseStream.named("integers")
    values = [stream.integer(index, 3, 7) for index in range(1_000)]

    assert set(values) == {3, 4, 5, 6, 7}
