import type { ComponentPropsWithoutRef, ElementType, ReactNode } from 'react';
import './Button.css';

/** `secondary` is the default because it is the default in practice: admin
    writes it three times for every `primary`. A page has one primary action
    and a page full of secondary ones. */
export type ButtonVariant = 'primary' | 'secondary' | 'danger';

export type ButtonProps<T extends ElementType = 'button'> = {
  /** What to render. A <button> by default; `a` for a control that navigates,
      or react-router's <Link> for one that navigates inside the app. An action
      that changes the URL is a link and has to be one — middle-click, copy
      link address and open-in-new-tab all come from the element, not from the
      paint. */
  as?: T;
  variant?: ButtonVariant;
  /** A request is in flight: the button stops taking presses and says so to
      assistive technology. It deliberately looks the same as `disabled` — the
      dimmed state admin has always shown while saving — because what a loading
      button should look like is a design decision this phase does not get to
      make. No spinner: calm means nothing moves unless a person moved it, and
      a spinner in every row of a table moves constantly. */
  loading?: boolean;
  className?: string;
  children?: ReactNode;
} & Omit<ComponentPropsWithoutRef<T>, 'as' | 'className' | 'children'>;

/**
 * The pressable thing. 110 sites in admin.
 *
 * `type` defaults to "button", and only when it is actually rendering one.
 * That is a change from a bare <button>, whose HTML default is "submit", and
 * it is the safe direction: admin has no <form> elements at all, so nothing
 * here submits one today, and a button that silently submits a form someone
 * adds later is a bug that is hard to see. A button that genuinely wants to
 * submit says so.
 */
export function Button<T extends ElementType = 'button'>({
  as,
  variant = 'secondary',
  loading = false,
  disabled,
  className,
  children,
  ...rest
}: ButtonProps<T>) {
  const Component = (as ?? 'button') as ElementType;
  const classes = ['aerie-btn', `aerie-btn--${variant}`];
  if (className) classes.push(className);

  // `disabled` and `type` mean nothing on an anchor - a disabled attribute on
  // an <a> is ignored by every browser - so they are set only on a real
  // button, and a link that must not be followed should not be rendered.
  const native =
    Component === 'button'
      ? { type: 'button' as const, disabled: disabled || loading }
      : {};

  return (
    <Component className={classes.join(' ')} aria-busy={loading || undefined} {...native} {...rest}>
      {children}
    </Component>
  );
}
