export async function verifyHelloFunction(baseUrl: string): Promise<void> {
  const url = baseUrl.endsWith("/") ? baseUrl : `${baseUrl}/`;
  const res = await fetch(url);
  if (!res.ok) throw new Error(`hello function HTTP ${res.status}`);
  const text = await res.text();
  if (!text.includes("Hello from Neon Functions")) {
    throw new Error(`Unexpected hello response: ${text.slice(0, 120)}`);
  }
  console.log("[functions] ok — hello responded");
}
