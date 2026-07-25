using System.Text;

sealed class StringBuilderReader(StringBuilder stringBuilder) : TextReader {
    int position;

    public override int Peek() {
        if (position == stringBuilder.Length) {
            return -1;
        }

        return stringBuilder[position];
    }

    public override int Read() {
        if (position == stringBuilder.Length) {
            return -1;
        }

        return stringBuilder[position++];
    }

    public override int Read(char[] buffer, int index, int count) {
        var charsToCopy = Math.Min(count, stringBuilder.Length - position);
        stringBuilder.CopyTo(position, buffer, index, charsToCopy);
        position += charsToCopy;
        
        return charsToCopy;
    }
}
