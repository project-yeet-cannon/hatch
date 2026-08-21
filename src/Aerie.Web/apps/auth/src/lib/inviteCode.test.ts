import { describe, expect, it } from 'vitest';
import { formatInviteBody, formatInviteCode, inviteCodeBody, isCompleteInviteCode } from './inviteCode';

// Every case here is a rule AuthTokens.NormalizeInviteCode also implements. If
// one of these ever has to change, that file changes in the same commit.
describe('inviteCodeBody', () => {
  it('keeps a bare code as it is', () => {
    expect(inviteCodeBody('K3M9P2QT')).toBe('K3M9P2QT');
  });

  it('accepts a paste of the whole formatted code', () => {
    expect(inviteCodeBody('AERIE-K3M9-P2QT')).toBe('K3M9P2QT');
  });

  it('accepts a code read aloud and typed with spaces', () => {
    expect(inviteCodeBody('k3m9 p2qt')).toBe('K3M9P2QT');
  });

  it('folds the Crockford substitutions, so a code heard as "eye" still lands', () => {
    expect(inviteCodeBody('I1L0OZ23')).toBe('11100Z23');
  });

  it('strips the prefix before folding, so the I in AERIE is not turned into a 1', () => {
    expect(inviteCodeBody('AERIEK3M9P2QT')).toBe('K3M9P2QT');
  });

  it('treats a half-typed prefix as nothing yet', () => {
    expect(inviteCodeBody('A')).toBe('');
    expect(inviteCodeBody('AERI')).toBe('');
    expect(inviteCodeBody('AERIE')).toBe('');
    expect(inviteCodeBody('AERIE-')).toBe('');
  });

  it('drops characters outside the alphabet rather than rejecting the whole value', () => {
    expect(inviteCodeBody('K3M9!P2Q@T')).toBe('K3M9P2QT');
  });

  it('stops at eight characters', () => {
    expect(inviteCodeBody('K3M9P2QTZZZZ')).toBe('K3M9P2QT');
  });

  it('is idempotent, since it runs again on every keystroke over its own output', () => {
    const once = inviteCodeBody('AERIE-K3M9-P2QT');
    expect(inviteCodeBody(once)).toBe(once);
  });
});

describe('isCompleteInviteCode', () => {
  it('is true only at the full length', () => {
    expect(isCompleteInviteCode('K3M9P2QT')).toBe(true);
    expect(isCompleteInviteCode('K3M9P2Q')).toBe(false);
    expect(isCompleteInviteCode('')).toBe(false);
  });
});

describe('formatting', () => {
  it('groups the body as it is typed', () => {
    expect(formatInviteBody('')).toBe('');
    expect(formatInviteBody('K3M')).toBe('K3M');
    expect(formatInviteBody('K3M9')).toBe('K3M9');
    expect(formatInviteBody('K3M9P')).toBe('K3M9-P');
    expect(formatInviteBody('K3M9P2QT')).toBe('K3M9-P2QT');
  });

  it('spells the whole code the way a person says it', () => {
    expect(formatInviteCode('K3M9P2QT')).toBe('AERIE-K3M9-P2QT');
  });
});
