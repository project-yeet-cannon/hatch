/*
  What is left here after the shared notices moved up to src/components/Notices
  - one piece, and the only one that was ever Storage's own.
*/

/**
 * The crate code, in the shape it's printed on the label. Tabular figures and a
 * mono face because this is a string people compare character by character
 * against a box, standing up, in bad light.
 */
export function CodeChip({ code }: { code: string }) {
  return <span className="crate-code">{code}</span>;
}
