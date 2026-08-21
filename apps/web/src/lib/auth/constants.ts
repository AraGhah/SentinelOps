export const SESSION_COOKIE_NAME = 'sentinelops_session';

// Short-lived cookie holding the pending MFA challenge (email + Cognito
// session token) between the login form submission and the MFA verify step.
// Never put the challenge session in a URL query string — see FE-03.
export const MFA_CHALLENGE_COOKIE_NAME = 'sentinelops_mfa_challenge';
