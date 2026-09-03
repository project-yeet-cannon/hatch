"""The fold arithmetic, asserted without running a backtest.

Deliberately separate from the walk-forward that walks these folds. A schedule
that overlapped its own train and test windows would leak future bars into a
parameter choice, which is the one failure the whole phase exists to prevent,
and it is a property of the dates alone - so it is checked against the dates
alone rather than inferred from a suspicious-looking Sharpe.
"""

from datetime import UTC, datetime, timedelta
from itertools import pairwise

import pytest

from aerie_trading.honesty.windows import folds_over, out_of_sample

START = datetime(2021, 1, 1, tzinfo=UTC)
END = datetime(2026, 1, 1, tzinfo=UTC)


def test_a_schedule_has_the_folds_it_was_asked_for() -> None:
    assert len(folds_over(START, END, 5)) == 5
    assert len(folds_over(START, END, 2)) == 2


def test_train_windows_never_reach_into_their_own_test_window() -> None:
    for fold in folds_over(START, END, 5):
        assert fold.train_start < fold.train_end
        assert fold.train_end == fold.test_start
        assert fold.test_start < fold.test_end


def test_test_windows_are_contiguous_and_do_not_overlap() -> None:
    schedule = folds_over(START, END, 5)

    for earlier, later in pairwise(schedule):
        assert earlier.test_end == later.test_start


def test_a_train_window_is_the_declared_multiple_of_its_test_window() -> None:
    for fold in folds_over(START, END, 5, train_multiple=3):
        train = fold.train_end - fold.train_start
        test = fold.test_end - fold.test_start
        # Not exact: the last segment absorbs the remainder of a span that does
        # not divide evenly, which is a difference of microseconds over five
        # years and is the price of every boundary being a date rather than a
        # bar count.
        assert abs(train - test * 3) < timedelta(seconds=1)


def test_the_schedule_covers_the_tail_of_the_window_exactly_once() -> None:
    schedule = folds_over(START, END, 5, train_multiple=3)
    span = out_of_sample(schedule)

    assert span.start == schedule[0].test_start
    assert span.end == END
    # Eight segments, five of which are tested: the held-out span is the last
    # five eighths of the window, and nothing before it is ever a test window.
    assert span.start == START + (END - START) * 3 // 8


def test_a_walk_forward_needs_more_than_one_fold() -> None:
    with pytest.raises(ValueError, match="at least two folds"):
        folds_over(START, END, 1)


def test_a_window_that_ends_before_it_starts_is_refused() -> None:
    with pytest.raises(ValueError, match="must end after it starts"):
        folds_over(END, START, 5)


def test_a_window_too_short_to_cut_is_refused_rather_than_collapsed() -> None:
    # Eight segments out of four microseconds is a segment of zero, and a
    # schedule of empty folds would run and report nothing rather than fail.
    with pytest.raises(ValueError, match="too short to cut"):
        folds_over(START, START + timedelta(microseconds=4), 5)
