import { describe, expect, it } from 'vitest';
import { nameSave, tokenClear, tokenIsSet, tokenPlaceholder, tokenSave } from './hatchSettings';
import type { HatchSettings } from '../types';

const settings = (over: Partial<HatchSettings> = {}): HatchSettings => ({
  claudeSubscriptionToken: '',
  localPersonNameApplies: true,
  localPersonName: '',
  ...over,
});

describe('the token field', () => {
  it('knows a token is stored from the dots, without ever holding one', () => {
    expect(tokenIsSet(settings({ claudeSubscriptionToken: '••••••••' }))).toBe(true);
    expect(tokenIsSet(settings())).toBe(false);
  });

  /* The sharp one. Putting the dots in the box would mean an operator who
     saved, came back and pressed Save stored eight bullets as their
     credential. */
  it('says a token is set in the placeholder, so the dots never become a value', () => {
    const hint = tokenPlaceholder(settings({ claudeSubscriptionToken: '••••••••' }));

    expect(hint).not.toContain('•');
    expect(hint).toContain('set');
  });

  it('says so when there is none', () => {
    expect(tokenPlaceholder(settings())).toBe('Not set');
  });

  it('sends nothing for a blank draft - leaving it alone is not clearing it', () => {
    expect(tokenSave('')).toBeNull();
    expect(tokenSave('   ')).toBeNull();
  });

  it('sends the trimmed token, and nothing else', () => {
    expect(tokenSave('  sk-ant-oat-1 ')).toEqual({ claudeSubscriptionToken: 'sk-ant-oat-1' });
  });

  /* An omitted field would leave it alone; `''` is what removes it. */
  it('clears with an empty string rather than an omitted field', () => {
    expect(tokenClear()).toEqual({ claudeSubscriptionToken: '' });
    expect(tokenClear().claudeSubscriptionToken).not.toBeUndefined();
  });

  it('never names the other setting, so one form cannot wipe the other', () => {
    expect(tokenSave('sk-ant-oat-1')).not.toHaveProperty('localPersonName');
    expect(tokenClear()).not.toHaveProperty('localPersonName');
  });
});

describe('the name field', () => {
  it('sends the trimmed name', () => {
    expect(nameSave('  Ada  ')).toEqual({ localPersonName: 'Ada' });
  });

  /* Unlike the token, this field shows what is stored - so an empty box is
     somebody looking at nothing and asking for nothing. */
  it('treats an emptied box as a clear', () => {
    expect(nameSave('')).toEqual({ localPersonName: '' });
  });

  it('never names the token', () => {
    expect(nameSave('Ada')).not.toHaveProperty('claudeSubscriptionToken');
  });
});
