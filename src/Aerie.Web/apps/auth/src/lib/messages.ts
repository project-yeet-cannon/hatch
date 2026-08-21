import type { RedeemFailure } from '../api/auth';

/**
 * What each refusal says out loud.
 *
 * The server distinguishes "expired" from "already used" from "not a code" on
 * purpose (see `AuthRedemption`), and the only reason to carry that distinction
 * across the wire is to say it here: the three send a person to three different
 * next actions - ask for a fresh one, ask for a fresh one because this one
 * enrolled something else, or look again at what they typed. A single "sign-in
 * failed" would throw all of that away.
 *
 * None of it tells an attacker anything a fifteen-minute single-use code hadn't
 * already conceded.
 */
export interface FailureMessage {
  title: string;
  detail: string;
}

export const FAILURE_MESSAGES: Record<RedeemFailure, FailureMessage> = {
  invalid_code: {
    title: "That code isn't one we know",
    detail: 'Check the eight characters against the ones you were given, then try again.',
  },
  expired: {
    title: 'That code has expired',
    detail: 'Codes are only good for a few minutes. Ask for a fresh one and this will work.',
  },
  already_redeemed: {
    title: 'That code has already been used',
    detail: 'Each code signs in one device. Ask for a fresh one for this device.',
  },
  rate_limited: {
    title: 'Too many attempts',
    detail: 'Wait a minute before trying again.',
  },
  unreachable: {
    title: "Couldn't reach Aerie",
    detail: 'That is more likely the network than the code. Check the connection and try again.',
  },
};
