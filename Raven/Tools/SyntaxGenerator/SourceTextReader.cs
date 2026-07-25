using Microsoft.CodeAnalysis.Text;

sealed class SourceTextReader(SourceText sourceText) : TextReader {
    int position;

    public override int Peek() {
        if (position == sourceText.Length) {
            return -1;
        }

        return sourceText[position];
    }

    public override int Read() {
        if (position == sourceText.Length) {
            return -1;
        }

        return sourceText[position++];
    }

    public override int Read(char[] buffer, int index, int count) {
        var charsToCopy = Math.Min(count, sourceText.Length - position);
        sourceText.CopyTo(position, buffer, index, charsToCopy);
        position += charsToCopy;
        
        return charsToCopy;
    }
}
