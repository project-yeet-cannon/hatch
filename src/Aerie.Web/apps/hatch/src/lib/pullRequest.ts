/* Drawing the pull request an issue is being reviewed at. */

import { truncate } from './text';

/**
 * How much of a URL the chip draws. Long enough that an ordinary forge URL -
 * a host, an owner, a repository, a number - arrives whole; short enough that
 * one with forty characters of query string on it does not push the dates off
 * the line.
 */
export const PR_TEXT_MAX = 52;

/**
 * The URL, in as few characters as still identify it.
 *
 * Only the noise comes off: the scheme, a `www.`, a trailing slash. What is
 * left is the part somebody reads to know which pull request this is, and the
 * whole URL is still in the link's `title` and in where it goes.
 *
 * Nothing here parses the path for an owner, a repository or a number. Every
 * forge spells those differently and Aerie has no opinion about whose an
 * operator uses - a label that only read one company's URLs would be a fact
 * about exactly one installation.
 */
export function pullRequestWords(url: string, max: number = PR_TEXT_MAX): string {
  const bare = url
    .trim()
    .replace(/^https?:\/\//i, '')
    .replace(/^www\./i, '')
    .replace(/\/+$/, '');

  return truncate(bare, max);
}
