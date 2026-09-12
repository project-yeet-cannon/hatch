import { pullRequestWords } from '../lib/pullRequest';

/**
 * Where the issue is being reviewed, as something to click.
 *
 * A chip, beside the dates, because it is the same kind of fact they are: one
 * small thing about the issue that belongs where somebody's eye already goes
 * for "what is the state of this". `MomentChip` is the shape it copies.
 *
 * Nothing at all when there is no pull request - not a greyed chip and not the
 * word "none". An issue that has not been delivered yet is not missing
 * anything, and a row of empty placeholders is how a page stops being read.
 */
export function PullRequestLink({ url }: { url: string | null }) {
  if (!url) return null;

  return (
    <a
      className="hatch-chip hatch-chip-link"
      href={url}
      title={url}
      /* Somebody clicking through to a review is leaving Hatch for a different
         tool, not navigating within the board - and the issue they were reading
         should still be here when they come back. */
      target="_blank"
      rel="noreferrer noopener"
    >
      <span className="hatch-chip-kind">Pull request</span>
      {pullRequestWords(url)}
    </a>
  );
}
