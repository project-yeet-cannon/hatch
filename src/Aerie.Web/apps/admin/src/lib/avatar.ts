/**
 * Shrinks a picked image to something worth storing as an avatar.
 *
 * This is a courtesy, not a control. The server caps the upload and sniffs the
 * bytes (Common/PersonPhoto.cs), so nothing here is load-bearing for safety -
 * it exists because the file a phone hands you is a 4 MB photo of a person
 * standing in a garden, and what the People page draws is a 40-pixel circle.
 * Sending the garden would work and would put four megabytes in a backup for
 * every household member.
 *
 * It fails open on purpose. A browser without `createImageBitmap`, a canvas
 * that refuses to encode, an image the decoder does not like - each one returns
 * the original file rather than an error, because the server is going to have
 * an opinion about those bytes anyway and its opinion is the one that counts.
 */

/** Longest edge of the stored image. Comfortably above any avatar the admin app draws, so a future larger rendering has pixels to use. */
const MAX_EDGE = 512;

/** JPEG/WebP quality. High enough that a face survives it, low enough that the file is measured in tens of kilobytes. */
const QUALITY = 0.85;

export async function downscaleImage(file: File): Promise<Blob> {
  // A GIF is the one format worth passing through untouched: it may be
  // animated, and a canvas would silently flatten it to the first frame - which
  // is not a smaller version of what someone picked, it is a different picture.
  if (file.type === 'image/gif') return file;

  try {
    const bitmap = await createImageBitmap(file);
    const scale = Math.min(1, MAX_EDGE / Math.max(bitmap.width, bitmap.height));

    // Already small enough. Re-encoding it would cost quality to save nothing.
    if (scale === 1) {
      bitmap.close();
      return file;
    }

    const canvas = document.createElement('canvas');
    canvas.width = Math.round(bitmap.width * scale);
    canvas.height = Math.round(bitmap.height * scale);

    const context = canvas.getContext('2d');
    if (!context) {
      bitmap.close();
      return file;
    }

    context.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
    bitmap.close();

    // WebP over JPEG: every browser that can run this app can encode it, and a
    // PNG with transparency does not acquire a black background on the way
    // through. The server accepts all four formats regardless.
    const encoded = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, 'image/webp', QUALITY));

    // Bigger than what we started with happens with small, flat images - keep
    // whichever is actually smaller rather than assuming the work helped.
    return encoded && encoded.size < file.size ? encoded : file;
  } catch {
    return file;
  }
}

/**
 * The one or two characters drawn in place of a photo.
 *
 * Uses `Intl.Segmenter` rather than `slice`, for the same reason PersonName
 * counts graphemes rather than chars: slicing a string whose first "letter" is
 * a family emoji yields half a surrogate pair, which renders as a replacement
 * character - so the person with the most deliberate name gets the most broken
 * avatar.
 */
export function initials(name: string): string {
  const first = firstGrapheme(name);
  const surname = name.split(' ').slice(1).find((part) => part.length > 0);

  return surname ? first + firstGrapheme(surname) : first;
}

function firstGrapheme(value: string): string {
  if (typeof Intl.Segmenter === 'function') {
    const [segment] = new Intl.Segmenter(undefined, { granularity: 'grapheme' }).segment(value);
    return segment?.segment ?? '';
  }

  // Older engines: take a whole code point rather than a code unit, which at
  // least keeps a plain emoji intact even if a joined sequence is truncated.
  return [...value][0] ?? '';
}
