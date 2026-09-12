/**
 * Which of the four published binaries this browser is standing on.
 *
 * Pure, and takes the user agent as an argument rather than reading
 * `navigator`, so it can be read - and tested - without a DOM. It answers a
 * .NET runtime identifier because that is what `RunnerDownload.rid` carries;
 * the page matches on the string rather than interpreting it.
 *
 * **A Mac is always Apple Silicon.** There is no reliable way to tell an M-
 * series Mac from an Intel one from a browser: `navigator.platform` says
 * "MacIntel" on both, and `navigator.userAgentData.getHighEntropyValues` -
 * which could answer - exists only on Chromium, so Safari and Firefox, between
 * them most of the Macs, have no answer at all. Guessing arm64 is right for
 * every Mac sold since 2020 and wrong in a way the page fixes with one click,
 * because the other three platforms are listed underneath. Guessing from a
 * detection method that half of browsers do not implement would be wrong in a
 * way nobody could see.
 *
 * Linux is also the fallback for anything unrecognised - a BSD, a bot, a user
 * agent somebody has rewritten. It is the platform a stranger is most likely to
 * be on, and the wrong guess costs one click either way.
 */
export function detectPlatform(userAgent: string): string {
  const agent = userAgent.toLowerCase();

  // Named rather than left to the fallback below, which would answer the same
  // thing: an Android user agent says Linux, there is no Android build to
  // offer, and a phone getting the desktop Linux binary should be a decision
  // somebody made rather than an accident of ordering.
  if (agent.includes('android')) return 'linux-x64';

  // iPhone and iPad included: nothing here runs on them, and "macOS" is the
  // closest true thing to say to somebody who opened Hatch on a phone. They
  // are reading the page, not the machine they will run the runner on.
  if (agent.includes('mac') || agent.includes('iphone') || agent.includes('ipad')) return 'osx-arm64';

  if (agent.includes('win')) return 'win-x64';

  return 'linux-x64';
}
