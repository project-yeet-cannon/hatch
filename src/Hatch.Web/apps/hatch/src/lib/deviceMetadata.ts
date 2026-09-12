// Non-standard fields a handful of browsers (mainly Chromium) expose beyond
// lib.dom's Navigator/Performance types.
interface NavigatorExtras extends Navigator {
  deviceMemory?: number;
  connection?: {
    effectiveType?: string;
    downlink?: number;
    rtt?: number;
    saveData?: boolean;
  };
  userAgentData?: {
    brands?: { brand: string; version: string }[];
    mobile?: boolean;
    platform?: string;
  };
}

interface PerformanceWithMemory extends Performance {
  memory?: {
    usedJSHeapSize: number;
    totalJSHeapSize: number;
    jsHeapSizeLimit: number;
  };
}

function toMb(bytes: number): number {
  return Math.round((bytes / 1_048_576) * 10) / 10;
}

/** Collected once per page load: things that won't change while the page stays open. */
export function collectStaticMetadata(): Record<string, unknown> {
  const nav = navigator as NavigatorExtras;

  return {
    userAgent: nav.userAgent,
    language: nav.language,
    languages: nav.languages,
    platform: nav.platform,
    hardwareConcurrency: nav.hardwareConcurrency,
    deviceMemoryGb: nav.deviceMemory,
    cookieEnabled: nav.cookieEnabled,
    userAgentData: nav.userAgentData
      ? { brands: nav.userAgentData.brands, mobile: nav.userAgentData.mobile, platform: nav.userAgentData.platform }
      : undefined,
    timezone: Intl.DateTimeFormat().resolvedOptions().timeZone,
    screen: {
      width: screen.width,
      height: screen.height,
      availWidth: screen.availWidth,
      availHeight: screen.availHeight,
      colorDepth: screen.colorDepth,
      orientation: screen.orientation?.type,
    },
    referrer: document.referrer || undefined,
  };
}

/** Recomputed on every log call: things that can change over the page's lifetime. */
export function collectDynamicMetadata(): Record<string, unknown> {
  const nav = navigator as NavigatorExtras;
  const perf = performance as PerformanceWithMemory;

  return {
    viewport: { width: window.innerWidth, height: window.innerHeight, devicePixelRatio: window.devicePixelRatio },
    onLine: nav.onLine,
    visibilityState: document.visibilityState,
    title: document.title,
    connection: nav.connection
      ? {
          effectiveType: nav.connection.effectiveType,
          downlinkMbps: nav.connection.downlink,
          rttMs: nav.connection.rtt,
          saveData: nav.connection.saveData,
        }
      : undefined,
    memory: perf.memory
      ? {
          usedJsHeapMb: toMb(perf.memory.usedJSHeapSize),
          totalJsHeapMb: toMb(perf.memory.totalJSHeapSize),
          jsHeapLimitMb: toMb(perf.memory.jsHeapSizeLimit),
        }
      : undefined,
  };
}
