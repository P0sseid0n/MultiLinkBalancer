// Durações das animações feitas em JS (transições e Tween do Svelte). As animações de CSS já são
// desligadas pelo app.css com "reduzir animações" do Windows; estas precisam zerar a duração aqui.
import { prefersReducedMotion } from 'svelte/motion';

export const dur = (ms) => (prefersReducedMotion.current ? 0 : ms);
