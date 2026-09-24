# Verified serial protocol

Model: TESmart HKS0802A1U / HKS402-E23. Settings: 9600 baud, 8 bits, no parity,
one stop bit, no hardware/software flow control. DTR and RTS are not asserted.

Select both monitors:

```text
AA BB 03 01 NN EE
```

`NN` is `01`, `02`, `03`, or `04`. No initialization command was needed once TX/RX
were correctly crossed and ground was connected.

| Actual received text | Interpreted selection |
| --- | --- |
| `Set ch is 0` | Computer 1 |
| `Set ch is 1` | Computer 2 |
| `Set ch is 2` | Computer 3 |
| `Set ch is 3` | Computer 4 |

Replies can repeat, arrive fragmented, and use CR CR LF line endings. The parser also
handles an observed merged line such as `QuerrySet ch is 0`. Front-panel button changes
produced the same unsolicited channel events.

Other observed diagnostics, retained without assigning undocumented meanings:

```text
Querry pc LED
Write EEPROM
led_status_update=00 idx=0
```

`Write EEPROM` likely indicates saving settings to nonvolatile memory; this is an
interpretation, not a verified read/write API. LED messages are not interpreted as
computer-online status. No independent status-query command is verified.

This project's status tracks channel events. It does not decode separate display routes,
keyboard/USB focus, autoscan, EDID state, power, or video presence. Avoid relying on a
single channel number after manually enabling split-display routing.

Primary references:
- [TESmart manual](https://support.tesmart.com/hc/en-us/article_attachments/53210339209369)
- [Model FAQ](https://support.tesmart.com/hc/en-us/articles/24748311049113-HKS402-E23-Previously-HKS0802A1U-FAQ)
- [Waveshare converter documentation](https://www.waveshare.com/wiki/USB_TO_RS232/485)
