/** Build the private GitHub capability URI exactly as .NET Uri.EscapeDataString does (RFC 3986). */
export function remoteWirePath(repositoryId: string, branch: string, path: string): string {
  return `github://${repositoryId}/${escapeRemoteComponent(branch)}/${escapeRemoteComponent(path)}`;
}

function escapeRemoteComponent(value: string): string {
  return encodeURIComponent(value).replace(
    /[!'()*]/g,
    (character) => `%${character.charCodeAt(0).toString(16).toUpperCase()}`,
  );
}
