import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';
import { viteSingleFile } from 'vite-plugin-singlefile';

// Gera um único index.html (JS e CSS embutidos) que o app C# serve de dentro do .exe.
export default defineConfig({
  plugins: [svelte(), viteSingleFile()],
  build: { target: 'chrome120', cssCodeSplit: false, assetsInlineLimit: 100000000 },
  server: { port: 5173, strictPort: true },
});
