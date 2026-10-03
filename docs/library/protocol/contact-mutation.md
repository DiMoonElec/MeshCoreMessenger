# Contact mutation

[Оглавление](../../COMPANION_PROTOCOL.md) · [Маршрутизация чтения](../../README.md)

## Contact mutation

`ADD_UPDATE_CONTACT (9)` uses the same contact fields as `CONTACT`, without the
response type byte and without a required `lastmod`:

```text
09 | public key (32) | type (1) | flags (1) | encoded path length (1)
   | path field (64) | NUL-padded UTF-8 name (32) | last advert u32
   | latitude i32 microdegrees | longitude i32 microdegrees
```

The library always sends this complete 144-byte form. It omits optional `lastmod`,
so current firmware assigns its own RTC time rather than trusting an application
timestamp. Names are limited to 31 UTF-8 bytes plus NUL. Paths use the packed
descriptor: low six bits are the hash count, upper two bits plus one are the hash
size; mode 3 is reserved, while `0xFF` means unknown/flood. The fixed path field is
always 64 bytes even when only a prefix is meaningful.

`REMOVE_CONTACT (15)` is followed by the complete 32-byte public key. Both commands
return `OK/ERROR`; add can report `TABLE_FULL`, and removal reports `NOT_FOUND` when
the key is absent. Neither command performs an RF transmission. Contact persistence
is scheduled lazily by firmware, while its in-memory list changes before `OK`.


`RESET_PATH (13)` принимает `0D | public key (32)` и возвращает OK/ERROR
(NOT_FOUND при отсутствующем контакте). Реализован `MeshCoreClient.ResetPathAsync`.
Firmware устанавливает encoded out_path_len в FF, оставляет байты пути и lastmod,
назначает lazy contact persistence. RF передачи и PATH_UPDATED push обработчик
сброса не выполняет. Следующая отправка использует flood, пока нода снова не
выучит маршрут. Приложение перечитывает контакт явно; библиотека не делает
implicit readback или retries. [Проверки](../../testing/private-route-reset.md).
