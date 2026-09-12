import type { ComponentPropsWithoutRef, ElementType, ReactNode } from 'react';
import './Text.css';

/** What the phrase means, not how loud it is. `default` inherits the ink it
    sits in, which is what a <Text> inside a table cell or a card wants. */
export type TextTone = 'default' | 'muted' | 'danger' | 'success';

export type TextProps<T extends ElementType = 'p'> = {
  tone?: TextTone;
  /** The element to render. A paragraph by default because that is what four
      fifths of admin's toned text already is; `span` for a phrase inside a
      sentence, `td` for a whole cell, `ol` for a list. */
  as?: T;
  className?: string;
  children?: ReactNode;
} & Omit<ComponentPropsWithoutRef<T>, 'as' | 'className' | 'children'>;

/**
 * Toned text — 126 sites in admin, and by a wide margin the most-used idiom in
 * the house.
 *
 * It is polymorphic rather than always a <span> because the tone belongs on
 * whatever element the content already wanted to be. Wrapping a <td>'s
 * contents in a <span> to color them would put an inline box inside every cell
 * with an opinion, and wrapping a paragraph in one would be worse.
 *
 * The element's own props — colSpan on a `td`, id, style, onClick — pass
 * through, so this replaces a className on an existing element without that
 * element losing anything.
 */
export function Text<T extends ElementType = 'p'>({ tone = 'default', as, className, children, ...rest }: TextProps<T>) {
  const Component = (as ?? 'p') as ElementType;
  const classes = [];
  if (tone !== 'default') classes.push(`hatch-text--${tone}`);
  if (className) classes.push(className);

  return (
    <Component className={classes.length ? classes.join(' ') : undefined} {...rest}>
      {children}
    </Component>
  );
}
