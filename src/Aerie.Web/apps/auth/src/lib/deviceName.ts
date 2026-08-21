/**
 * A first guess at what to call this device, so the admin Sessions page reads
 * like a list of things in a house rather than a wall of `Mozilla/5.0`.
 *
 * It is a guess and the field it fills is editable, which is the point: the
 * name a household actually wants is "Ada's iPhone", and no user agent has ever
 * carried the "Ada's" part. Getting the noun right is the whole job - it turns
 * the field from something to compose into something to confirm.
 */

interface NavigatorUAData {
  mobile?: boolean;
  platform?: string;
}

/** What this device most likely is, e.g. "iPhone" or "Windows PC". Never empty. */
export function guessDeviceName(userAgent: string = navigator.userAgent): string {
  // iPadOS 13+ reports itself as a Mac and is distinguished only by having a
  // touch screen - without this every family iPad enrolls as "Mac".
  if (/iPad/.test(userAgent) || (/Macintosh/.test(userAgent) && navigator.maxTouchPoints > 1)) return 'iPad';
  if (/iPhone/.test(userAgent)) return 'iPhone';
  if (/iPod/.test(userAgent)) return 'iPod touch';

  if (/Android/.test(userAgent)) {
    // Android's UA carries the marketing model between the build tag and the
    // trailing "Build/..." - "Pixel 8" or "SM-A536B". Worth lifting: it is the
    // difference between two identical-looking rows and two nameable tablets.
    const model = /;\s*([^;)]+?)\s*(?:Build\/[^;)]*)?\)/.exec(userAgent)?.[1];
    if (model && !/^Android/i.test(model) && model.length <= 32) return model;

    // "Mobile" is present on phones and absent on tablets - Android's own
    // documented way of telling them apart from the UA.
    return /Mobile/.test(userAgent) ? 'Android phone' : 'Android tablet';
  }

  if (/CrOS/.test(userAgent)) return 'Chromebook';
  if (/Macintosh/.test(userAgent)) return 'Mac';
  if (/Windows/.test(userAgent)) return 'Windows PC';
  if (/Linux/.test(userAgent)) return 'Linux PC';

  const platform = (navigator as Navigator & { userAgentData?: NavigatorUAData }).userAgentData?.platform;
  return platform || 'New device';
}
