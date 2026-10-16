namespace MultiLink.App
{
    // O que o AppController precisa da janela. Implementado pela Ui (MainWindow); sempre chamado na thread da interface.
    public interface IAppHost
    {
        void Notify(string title, string message, bool alert);  // aviso do Windows (balão da bandeja)
        void Push(object message);                              // mensagem para a interface web
        string PickSavePath(string suggestedName);              // diálogo "Salvar como"; null se cancelado
        void SetHotkeys(bool enabled);
        void Exit();
    }
}
