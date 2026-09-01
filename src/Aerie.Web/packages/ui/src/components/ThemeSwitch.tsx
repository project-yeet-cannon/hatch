import { useId } from 'react';
import { useTheme } from '../theme/useTheme';
import type { ThemeChoice } from '../theme/themeContext';
import './ThemeSwitch.css';

/** The ground the switch is sitting on. `surface` is a page or a card;
    `accent` is a filled bar, where the ink has to come from --on-accent or it
    is a grey pill on a blue field. */
export type ThemeSwitchTone = 'surface' | 'accent';

const CHOICES: { value: ThemeChoice; label: string }[] = [
  { value: 'auto', label: 'Auto' },
  { value: 'light', label: 'Light' },
  { value: 'dark', label: 'Dark' },
];

/**
 * The control for the mechanism Phase 1 shipped: `auto` defers to the OS,
 * `light`/`dark` override it and persist.
 *
 * Built on real radio inputs rather than buttons with `role="radio"`, because
 * the browser then supplies the whole keyboard contract for free - one tab
 * stop for the group, arrow keys to move within it, and the grouping announced
 * to a screen reader off the <fieldset>/<legend> rather than off an ARIA
 * attribute someone has to remember to keep in sync.
 *
 * `name` is derived from useId rather than a constant so two switches on one
 * page (the gallery shows components in several contexts) stay independent
 * groups instead of silently stealing each other's checked state.
 */
export function ThemeSwitch({
  tone = 'surface',
  className,
}: {
  tone?: ThemeSwitchTone;
  className?: string;
}) {
  const { choice, setChoice } = useTheme();
  const name = useId();
  const classes = ['aerie-theme-switch', `aerie-theme-switch--${tone}`];
  if (className) classes.push(className);

  return (
    <fieldset className={classes.join(' ')}>
      <legend className="aerie-theme-switch__legend">Theme</legend>
      {CHOICES.map((option) => (
        <label key={option.value} className="aerie-theme-switch__option">
          <input
            type="radio"
            name={name}
            value={option.value}
            checked={choice === option.value}
            onChange={() => setChoice(option.value)}
          />
          <span>{option.label}</span>
        </label>
      ))}
    </fieldset>
  );
}
