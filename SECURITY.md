# Segurança

## Como relatar uma falha

Encontrou uma falha de segurança no MultiLink Balancer? Escreva para **contato@p0sseid0n.dev** em vez de abrir uma
issue pública. Assim dá para corrigir antes que a falha fique exposta para todo mundo.

Ajuda incluir:

- a versão do app (Propriedades do `.exe` › Detalhes) e a do Windows;
- o que acontece e como reproduzir;
- o que alguém conseguiria fazer explorando a falha.

A resposta vem pelo mesmo e-mail. Depois da correção publicada, a falha pode ser descrita nas notas da versão, com
crédito para quem relatou, se a pessoa quiser.

## Versões cobertas

Só a versão mais recente, publicada em
[Releases](https://github.com/P0sseid0n/MultiLinkBalancer/releases/latest). As correções saem em versões novas, e o app avisa
quando há uma (a atualização fica em *Ajustes*).

## O que interessa em especial

O app mexe no proxy do Windows, nas rotas e, com a captura ligada, roda como administrador com um adaptador virtual
(Wintun). Falhas nessas partes são as mais importantes, por exemplo:

- outro PC da rede conseguir usar o proxy local (`127.0.0.1:1080`), ou uma página aberta na janela do app conseguir mandar comandos para ele;
- tráfego que deveria passar pelo app sair por fora dele, ou o contrário;
- a atualização aceitar um `.exe` que não seja o publicado na release (a conferência do SHA-256 falhar);
- o proxy do Windows, as rotas ou o adaptador ficarem alterados depois de sair do app.
