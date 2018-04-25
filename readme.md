#### Brisk Dotnet

Datagram (UDP) client connection.

Proprietary protocol that handles connection, time sync, prevents packet duplication and reordering, and disconnect-detection.

Uses [Log-Dotnet](https://github.com/Piot/log-dotnet) for logging and [Brook](https://github.com/Piot/brook-dotnet) for octet streams.

##### Usage

```csharp
public Connector(ILog log, IReceiveStream receiveStream);
public void Connect(string hostAndPort);
```

```csharp
public interface IReceiveStream
{
    void Lost();
    void Receive(IInOctetStream stream);
}
```

##### Example

```csharp
void ExampleMethod()
{
    var connector = new Connector(log, this);
    connector.Connect("example.com:32001");
}
```
