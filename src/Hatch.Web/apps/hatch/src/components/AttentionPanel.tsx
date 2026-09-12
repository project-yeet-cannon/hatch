import { Link } from 'react-router-dom';
import { questionEmptyWords, reviewEmptyWords, waitedWords } from '../lib/attention';
import { pullRequestWords } from '../lib/pullRequest';
import type { Attention } from '../types';

/**
 * What the control hands over when it is pressed: the links that unblock the
 * loop, in the order somebody would work through them.
 *
 * Both sections are always drawn, empty state included. A panel whose sections
 * appeared and disappeared would be a panel whose shape has to be re-read every
 * time it opens - and the empty states are not filler here: one of them is the
 * only place in Hatch that says an issue has sat in review with nowhere to
 * review it.
 */
export function AttentionPanel({ attention, now }: { attention: Attention | null; now: Date }) {
  const reviews = attention?.reviews ?? [];
  const questions = attention?.questions ?? [];

  return (
    <div className="hatch-attention-panel">
      <section className="hatch-attention-section">
        <h2 className="hatch-attention-heading">Pull requests to review</h2>

        {reviews.length === 0 ? (
          <p className="hatch-attention-empty">{reviewEmptyWords(attention)}</p>
        ) : (
          <ul className="hatch-attention-rows">
            {reviews.map((r) => (
              <li key={r.key}>
                {/* Out of Hatch and into a different tool, so a new tab and a
                    plain anchor - the board somebody was reading should still
                    be here when they come back. The same call PullRequestLink
                    makes, for the same reason. */}
                <a
                  className="hatch-attention-row"
                  href={r.pullRequestUrl}
                  title={r.pullRequestUrl}
                  target="_blank"
                  rel="noreferrer noopener"
                >
                  <span className="hatch-attention-row-head">
                    <span className="hatch-attention-key">{r.key}</span>
                    <span className="hatch-attention-title">{r.title}</span>
                  </span>
                  {/* Drawn the way the issue page's chip draws it: the noise
                      off the front, nothing parsed out of the path. Hatch has
                      no opinion about whose forge an operator uses. */}
                  <span className="hatch-attention-url">{pullRequestWords(r.pullRequestUrl)}</span>
                </a>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className="hatch-attention-section">
        <h2 className="hatch-attention-heading">Questions to answer</h2>

        {questions.length === 0 ? (
          <p className="hatch-attention-empty">{questionEmptyWords()}</p>
        ) : (
          <ul className="hatch-attention-rows">
            {questions.map((q) => (
              <li key={q.id}>
                {/* This tab, and to the issue rather than to anything here: a
                    question is answered on the issue page, with the options it
                    was asked with. Answering from the panel is out of scope on
                    purpose. */}
                <Link className="hatch-attention-row" to={`/issues/${q.issueKey}`}>
                  <span className="hatch-attention-row-head">
                    <span className="hatch-attention-key">{q.issueKey}</span>
                    <span className="hatch-attention-waited">{waitedWords(q.askedAt, now)}</span>
                  </span>
                  <span className="hatch-attention-question">{q.body}</span>
                </Link>
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}
