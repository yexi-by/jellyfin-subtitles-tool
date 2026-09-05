import { defineConfig } from 'vite';
export default defineConfig({ build: { target: 'es2020', lib: { entry: 'src/main.ts', name: 'SubtitlesTool', formats: ['iife'], fileName: () => 'subtitles-tool.js' }, sourcemap: false } });
