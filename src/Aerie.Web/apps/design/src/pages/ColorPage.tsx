import { GalleryPage, GallerySection } from '../components/Gallery';
import { Swatch } from '../components/Swatch';
import { useTokenValues } from '../lib/useTokenValues';

interface ColorGroup {
  title: string;
  note?: string;
  tokens: { name: string; use: string }[];
}

const GROUPS: ColorGroup[] = [
  {
    title: 'Surfaces',
    note: 'Two levels, not a stack. A card sits on the page; nothing sits on a card.',
    tokens: [
      { name: '--bg', use: 'The page behind everything' },
      { name: '--card', use: 'A raised surface: cards, modals, popovers' },
    ],
  },
  {
    title: 'Ink',
    tokens: [
      { name: '--ink', use: 'Body text and headings' },
      { name: '--muted', use: 'Secondary text: labels, timestamps, help' },
    ],
  },
  {
    title: 'Lines',
    note: 'Two weights. --line divides; --line-strong is a line that has to carry weight against --card.',
    tokens: [
      { name: '--line', use: 'Borders, dividers, the resting secondary button' },
      { name: '--line-strong', use: 'A pressed control, a divider on --card' },
    ],
  },
  {
    title: 'Primary',
    note: 'Every accent is a triple: the accent, a wash of it, and a darker ink for hover and text on a light ground.',
    tokens: [
      { name: '--primary', use: 'The action a page is for' },
      { name: '--primary-bg', use: 'A wash: selection, focus ring, chart fill' },
      { name: '--primary-ink', use: 'Hover, and primary-colored text' },
    ],
  },
  {
    title: 'Danger',
    tokens: [
      { name: '--danger', use: 'Destructive actions and failure states' },
      { name: '--danger-bg', use: 'The wash behind an error' },
      { name: '--danger-ink', use: 'Hover, and error text' },
    ],
  },
  {
    title: 'Success',
    tokens: [
      { name: '--success', use: 'Healthy, connected, saved' },
      { name: '--success-bg', use: 'The wash behind a confirmation' },
      { name: '--success-ink', use: 'Hover, and success text' },
    ],
  },
  {
    title: 'Ink on an accent',
    note: 'These flip with the theme rather than being white forever: the dark accents are light blues and corals, and white on them is a label nobody can read.',
    tokens: [
      { name: '--on-accent', use: 'A primary button’s label' },
      { name: '--on-accent-wash', use: 'The same ink at hover strength' },
    ],
  },
  {
    title: 'Fixed',
    note: 'Black in both themes. It is the absence of picture, not a surface, so it does not follow the palette.',
    tokens: [{ name: '--letterbox', use: 'Behind a video or image whose aspect is not the frame’s' }],
  },
];

const ALL_TOKENS = GROUPS.flatMap((group) => group.tokens.map((token) => token.name));

/* The three accents, laid out the way they are actually used: a filled block
   with its --on-accent label. This is the only honest way to show an ink token
   - a swatch of near-white on --card says nothing about whether it is legible
   where it lands. */
const ACCENTS = [
  { fill: '--primary', label: 'Primary' },
  { fill: '--danger', label: 'Danger' },
  { fill: '--success', label: 'Success' },
];

export function ColorPage() {
  const values = useTokenValues(ALL_TOKENS);

  return (
    <GalleryPage
      title="Color"
      blurb="Admin's existing vocabulary, carried forward. Every value here is authored in both themes - switch the theme in the sidebar and read them again."
    >
      {GROUPS.map((group) => (
        <GallerySection key={group.title} title={group.title} note={group.note}>
          <div className="swatch-grid">
            {group.tokens.map((token) => (
              <Swatch key={token.name} token={token.name} value={values[token.name]} use={token.use} />
            ))}
          </div>
        </GallerySection>
      ))}

      <GallerySection
        title="In context"
        note="Ink on an accent, at the size it is read. If a label here is hard to read in either theme, the pair is wrong."
      >
        <div className="accent-row">
          {ACCENTS.map((accent) => (
            <div
              key={accent.fill}
              className="accent-block"
              style={{ background: `var(${accent.fill})`, color: 'var(--on-accent)' }}
            >
              {accent.label}
            </div>
          ))}
        </div>
      </GallerySection>
    </GalleryPage>
  );
}
