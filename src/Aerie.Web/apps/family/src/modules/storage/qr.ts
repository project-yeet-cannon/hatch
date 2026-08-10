import { useEffect, useRef, useState } from 'react';

/**
 * QR generation for label sheets. Client-side, like apps/admin's kiosk
 * provisioning screen - nothing about a QR needs the server, and the sheet is
 * already holding everything it encodes.
 */

const OPTIONS = {
  // A PNG rather than an SVG because a QR *is* pixel art: 512px over a ~50mm
  // cell is ~260dpi, past what any scanner resolves, and every print engine
  // agrees on how to put a bitmap on paper.
  width: 512,
  // The sheet draws its own quiet zone in CSS padding, so the image doesn't
  // carry one - otherwise the visible code shrinks inside its cell for nothing.
  margin: 0,
  // Q (25% recoverable) rather than the M default. These get taped to boxes in
  // a garage: scuffs, dust and a photocopier are the expected conditions, and
  // the cost is a slightly denser code.
  errorCorrectionLevel: 'Q',
} as const;

export interface QrCodes {
  /** data: URLs keyed by the URL encoded, or null until every one is ready. */
  images: Record<string, string> | null;
  error: string | null;
}

/**
 * Renders every URL in one pass and reports readiness for all of them at once,
 * which is what lets the print flow wait: firing the print dialog while half
 * the images are still empty prints half a sheet of blank squares.
 */
export function useQrCodes(urls: string[]): QrCodes {
  const [state, setState] = useState<QrCodes>({ images: null, error: null });

  // The array is a fresh identity every render, so its contents are the
  // dependency and the array itself is read through a ref - the same shape
  // useResource uses for its loader, and for the same reason.
  const key = urls.join('\n');
  const latest = useRef(urls);
  latest.current = urls;

  useEffect(() => {
    let live = true;
    setState({ images: null, error: null });

    Promise.all(latest.current.map((url) => render(url).then((image) => [url, image] as const))).then(
      (pairs) => {
        if (live) setState({ images: Object.fromEntries(pairs), error: null });
      },
      (err: unknown) => {
        if (live) setState({ images: null, error: err instanceof Error ? err.message : String(err) });
      },
    );

    return () => {
      live = false;
    };
  }, [key]);

  return state;
}

/**
 * Resolves once the image is not just generated but *decoded*, so "ready" means
 * it can be painted. Without the decode, opening the print dialog the moment
 * the data URLs exist can catch the sheet a frame early and print blanks.
 */
async function render(url: string): Promise<string> {
  // Imported here rather than at the top of the file so the ~30 kB QR encoder
  // is its own chunk: it's needed at a desk when printing a sheet, and never on
  // the path this app is judged by - pointing a camera at a box in a garage.
  const { default: QRCode } = await import('qrcode');

  const image = await QRCode.toDataURL(url, OPTIONS);
  const element = new Image();
  element.src = image;
  await element.decode();
  return image;
}
