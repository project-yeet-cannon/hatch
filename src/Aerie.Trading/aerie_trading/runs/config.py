"""How the queue and its workers behave, as values rather than code.

Reachable as ``TRADING_RUNS__<field>``, or replaceable wholesale with JSON in
``TRADING_RUNS`` - the shape ``SyntheticConfig`` and ``CollectionConfig``
already have on ``Settings``, so an operator learns the convention once.

The two numbers worth understanding before changing either:

``lease_seconds``
    the visibility timeout. A run held longer than this is assumed lost and is
    handed to somebody else. Too short and a slow-but-alive worker has its work
    taken from it - which costs the duplicated compute, and *only* that, because
    the lease fence means the loser's result is discarded rather than written
    twice. Too long and a genuinely dead worker's run sits idle for that long
    before anyone retries it. Fifteen minutes is far above any backtest this
    silo has run and far below the patience of anyone watching a sweep.

``max_sweep_runs``
    the ceiling on one enqueue. The plan calls a sweep *"deliberately unbounded
    compute"* and this is where that stops being literally true: a mistyped
    step turns a 200-run grid into a 200,000-run one, and the difference
    between those two is invisible in the command that launches them. The
    estimate-and-confirm handshake in ``runs/sweep.py`` is the other half; this
    is the half that does not depend on the operator reading the estimate.
"""

from __future__ import annotations

from pydantic import BaseModel, ConfigDict, Field

__all__ = ["RunnerConfig"]


class RunnerConfig(BaseModel):
    """The worker loop's and the queue's whole configuration."""

    model_config = ConfigDict(frozen=True, extra="forbid")

    #: How long a claimed run is held before the reaper may hand it on.
    lease_seconds: int = Field(default=900, ge=10, le=86_400)

    #: How long a worker waits after finding the queue empty. Two seconds: a
    #: sweep is launched by a person who is watching, and the alternative to
    #: polling - LISTEN/NOTIFY - is a second mechanism to reason about for a
    #: latency nobody is measuring.
    poll_seconds: float = Field(default=2.0, gt=0, le=300)

    #: How many times a run may be attempted before it is failed for good.
    #: Three, because the failures this retries are a worker being evicted and
    #: a database failing over, and neither of those happens three times in a
    #: row without the fourth attempt being pointless.
    max_attempts: int = Field(default=3, ge=1, le=10)

    #: How long a run that *raised* waits before it is claimable again. A flat
    #: delay rather than an exponential backoff: the retry budget is three, so
    #: the difference between the two shapes is one interval, and a flat number
    #: is one an operator can predict while watching a sweep.
    #:
    #: It deliberately does not apply to a run whose *lease* expired. That is
    #: not a failure - nobody is holding the run - so waiting would delay a
    #: sweep for the length of a node drain and protect against nothing.
    retry_backoff_seconds: int = Field(default=30, ge=0, le=3_600)

    #: The ceiling on one enqueue. See the module docstring.
    max_sweep_runs: int = Field(default=50_000, ge=1, le=1_000_000)

    #: How many distinct histories a worker keeps in memory. A sweep's runs
    #: share one window and one universe, so 2 is enough for every run after
    #: the first to be a cache hit while a worker that drifts onto a second
    #: sweep does not thrash. Reading the lake per run instead would make a
    #: 1,000-run sweep a thousand DuckDB queries over the same Parquet.
    history_cache: int = Field(default=2, ge=1, le=32)

    #: What a worker calls itself in ``run.leased_by``. Empty means "use the
    #: hostname", which in a pod is the pod name - the one string that makes
    #: `kubectl logs` of the worker that held a run a copy-and-paste rather
    #: than a search.
    worker_name: str = ""
