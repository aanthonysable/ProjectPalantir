import {
  PublicClientApplication,
  type AuthenticationResult,
  type Configuration,
} from '@azure/msal-browser'
import type { EntraProviderConfig } from './api'

let pca: PublicClientApplication | null = null
let pcaKey = ''

function configKey(provider: EntraProviderConfig) {
  return `${provider.authority}|${provider.clientId}|${provider.scopes.join(' ')}`
}

async function getPca(provider: EntraProviderConfig): Promise<PublicClientApplication> {
  const key = configKey(provider)
  if (pca && pcaKey === key) {
    return pca
  }

  const config: Configuration = {
    auth: {
      clientId: provider.clientId,
      authority: provider.authority,
      redirectUri: window.location.origin,
      knownAuthorities: provider.authority.includes('ciamlogin.com')
        ? [new URL(provider.authority).hostname]
        : undefined,
    },
    cache: {
      cacheLocation: 'sessionStorage',
    },
  }

  pca = new PublicClientApplication(config)
  pcaKey = key
  await pca.initialize()
  return pca
}

export async function signInWithEntra(
  provider: EntraProviderConfig,
): Promise<{ idToken?: string; accessToken?: string }> {
  const client = await getPca(provider)
  const result: AuthenticationResult = await client.loginPopup({
    scopes: [...provider.scopes],
    prompt: 'select_account',
  })

  return {
    idToken: result.idToken,
    accessToken: result.accessToken,
  }
}
