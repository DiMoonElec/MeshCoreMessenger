# M1 — Contacts multi-frame transaction

[Оглавление](../../ROADMAP.md) · [Маршрутизация чтения](../../README.md)

## M1 — Contacts multi-frame transaction

Implemented (automated regression tests, local TCP self-test, and real-device contacts retrieval confirmed by the project owner):

- contact wire model/parser;
- `CONTACT_START` parser;
- `CONTACT` parser;
- `CONTACT_END` parser;
- `ContactsTransaction` state machine;
- `MeshCoreClient.GetContactsAsync()`;
- public `Contact` model;
- stream inactivity timeout behavior;
- tests where push packets are interleaved between contact frames.

Definition of done:

```text
GET_CONTACTS
<- CONTACT_START
<- CONTACT
<- MESSAGES_WAITING
<- CONTACT
<- ADVERTISEMENT
<- CONTACT_END
```

returns the complete contact collection and publishes the push packets without breaking the transaction.
